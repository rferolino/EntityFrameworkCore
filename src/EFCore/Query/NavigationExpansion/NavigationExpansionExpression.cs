// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.Expressions.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.NavigationExpansion.Visitors;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion
{
    public class NavigationExpansionExpression : Expression, IPrintable
    {
        private Type _returnType;

        public NavigationExpansionExpression(
            Expression operand,
            NavigationExpansionExpressionState state,
            Type returnType)
        {
            Operand = operand;
            State = state;
            _returnType = returnType;
        }

        public virtual Expression Operand { get; }
        public virtual NavigationExpansionExpressionState State { get; private set; }

        public override ExpressionType NodeType => ExpressionType.Extension;
        public override Type Type => _returnType;
        public override bool CanReduce => true;
        public override Expression Reduce()
        {
            var includeResult = ApplyIncludes();
            State = includeResult.state;
            var result = includeResult.operand;

            if (!State.ApplyPendingSelector
                && State.PendingOrderings.Count == 0
                && State.PendingTags.Count == 0
                && State.PendingCardinalityReducingOperator == null
                && State.MaterializeCollectionNavigation == null)
            {
                return result;
            }

            var parameter = Parameter(result.Type.GetSequenceType());

            foreach (var pendingOrdering in State.PendingOrderings)
            {
                var remappedKeySelectorBody = new ExpressionReplacingVisitor(pendingOrdering.keySelector.Parameters[0], State.CurrentParameter).Visit(pendingOrdering.keySelector.Body);
                var newSelectorBody = new NavigationPropertyUnbindingVisitor(State.CurrentParameter).Visit(remappedKeySelectorBody);
                var newSelector = Lambda(newSelectorBody, State.CurrentParameter);
                var orderingMethod = pendingOrdering.method.MakeGenericMethod(State.CurrentParameter.Type, newSelectorBody.Type);
                result = Call(orderingMethod, result, newSelector);
            }

            if (State.ApplyPendingSelector)
            {
                var pendingSelector = (LambdaExpression)new NavigationPropertyUnbindingVisitor(State.CurrentParameter).Visit(State.PendingSelector);
                var pendingSelectorBodyType = pendingSelector.Type.GetGenericArguments()[1];
                
                var pendingSelectMathod = result.Type.IsGenericType && (result.Type.GetGenericTypeDefinition() == typeof(IEnumerable<>) || result.Type.GetGenericTypeDefinition() == typeof(IOrderedEnumerable<>))
                    ? LinqMethodHelpers.EnumerableSelectMethodInfo.MakeGenericMethod(parameter.Type, pendingSelectorBodyType)
                    : LinqMethodHelpers.QueryableSelectMethodInfo.MakeGenericMethod(parameter.Type, pendingSelectorBodyType);

                result = Call(pendingSelectMathod, result, pendingSelector);
                parameter = Parameter(result.Type.GetSequenceType());
            }

            if (State.PendingTags.Count > 0)
            {
                var withTagMethodInfo = EntityFrameworkQueryableExtensions.TagWithMethodInfo.MakeGenericMethod(parameter.Type);
                foreach (var pendingTag in State.PendingTags)
                {
                    result = Call(withTagMethodInfo, result, Constant(pendingTag));
                }
            }

            if (State.PendingCardinalityReducingOperator != null)
            {
                var terminatingOperatorMethodInfo = State.PendingCardinalityReducingOperator.MakeGenericMethod(parameter.Type);

                result = Call(terminatingOperatorMethodInfo, result);
            }

            if (State.MaterializeCollectionNavigation != null)
            {
                var entityType = State.MaterializeCollectionNavigation.ClrType.IsGenericType
                    ? State.MaterializeCollectionNavigation.ClrType.GetGenericArguments()[0]
                    : State.MaterializeCollectionNavigation.GetTargetType().ClrType;

                result = Call(
                    NavigationExpansionHelpers.MaterializeCollectionNavigationMethodInfo.MakeGenericMethod(
                        State.MaterializeCollectionNavigation.ClrType,
                        entityType),
                    result,
                    Constant(State.MaterializeCollectionNavigation));
            }

            if (_returnType != result.Type && _returnType.IsGenericType)
            {
                if (_returnType.GetGenericTypeDefinition() == typeof(IOrderedQueryable<>))
                {
                    var toOrderedQueryableMethodInfo = typeof(NavigationExpansionExpression).GetMethod(nameof(NavigationExpansionExpression.ToOrderedQueryable)).MakeGenericMethod(parameter.Type);

                    return Call(toOrderedQueryableMethodInfo, result);
                }
                else if(_returnType.GetGenericTypeDefinition() == typeof(IOrderedEnumerable<>))
                {
                    var toOrderedEnumerableMethodInfo = typeof(NavigationExpansionExpression).GetMethod(nameof(NavigationExpansionExpression.ToOrderedEnumerable)).MakeGenericMethod(parameter.Type);

                    return Call(toOrderedEnumerableMethodInfo, result);
                }
                else if (_returnType.GetGenericTypeDefinition() == typeof(IIncludableQueryable<,>))
                {
                    // TODO: handle this using adapter, just like we do for order by?
                    return Convert(result, _returnType);
                }
            }

            return result;
        }

        private (Expression operand, NavigationExpansionExpressionState state) ApplyIncludes()
        {
            var includeFinder = new PendingIncludeFindingVisitor();
            includeFinder.Visit(State.PendingSelector.Body);

            var includeRewriter = new PendingSelectorIncludeRewriter();
            var rewrittenBody = includeRewriter.Visit(State.PendingSelector.Body);

            if (State.PendingSelector.Body != rewrittenBody)
            {
                State.PendingSelector = Lambda(rewrittenBody, State.PendingSelector.Parameters[0]);
                State.ApplyPendingSelector = true;
            }

            if (includeFinder.PendingIncludes.Count > 0)
            {
                var result = (source: Operand, parameter: State.CurrentParameter);
                foreach (var pendingIncludeNode in includeFinder.PendingIncludes)
                {
                    result = NavigationExpansionHelpers.AddNavigationJoin(
                        result.source,
                        result.parameter,
                        pendingIncludeNode.Value,
                        pendingIncludeNode.Key,
                        State,
                        new List<INavigation>(),
                        include: true);
                }

                var pendingSelector = State.PendingSelector;
                if (State.CurrentParameter != result.parameter)
                {
                    var pendingSelectorBody = new ExpressionReplacingVisitor(State.CurrentParameter, result.parameter).Visit(State.PendingSelector.Body);
                    pendingSelector = Lambda(pendingSelectorBody, result.parameter);
                }

                var newState = new NavigationExpansionExpressionState(
                    result.parameter,
                    State.SourceMappings,
                    pendingSelector,
                    applyPendingSelector: true,
                    State.PendingOrderings,
                    State.PendingIncludeChain,
                    State.PendingCardinalityReducingOperator,
                    State.PendingTags,
                    State.CustomRootMappings,
                    State.MaterializeCollectionNavigation);

                return (operand: result.source, state: newState);
            }

            return (operand: Operand, state: State);
        }

        public virtual void Print(ExpressionPrinter expressionPrinter)
        {
            expressionPrinter.Visit(Operand);

            if (State.ApplyPendingSelector)
            {
                expressionPrinter.StringBuilder.Append(".PendingSelect(");
                expressionPrinter.Visit(State.PendingSelector);
                expressionPrinter.StringBuilder.Append(")");
            }

            if (State.PendingCardinalityReducingOperator != null)
            {
                expressionPrinter.StringBuilder.Append(".Pending" + State.PendingCardinalityReducingOperator.Name);
            }
        }

        public static IOrderedQueryable<TElement> ToOrderedQueryable<TElement>(IQueryable<TElement> source)
            => new IOrderedQueryableAdapter<TElement>(source);

        private class IOrderedQueryableAdapter<TElement> : IOrderedQueryable<TElement>
        {
            IQueryable<TElement> _source;

            public IOrderedQueryableAdapter(IQueryable<TElement> source)
            {
                _source = source;
            }

            public Type ElementType => _source.ElementType;

            public Expression Expression => _source.Expression;

            public IQueryProvider Provider => _source.Provider;

            public IEnumerator<TElement> GetEnumerator()
                => _source.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator()
                => ((IEnumerable)_source).GetEnumerator();
        }

        public static IOrderedEnumerable<TElement> ToOrderedEnumerable<TElement>(IEnumerable<TElement> source)
            => new IOrderedEnumerableAdapter<TElement>(source);

        private class IOrderedEnumerableAdapter<TElement> : IOrderedEnumerable<TElement>
        {
            IEnumerable<TElement> _source;

            public IOrderedEnumerableAdapter(IEnumerable<TElement> source)
            {
                _source = source;
            }

            public IOrderedEnumerable<TElement> CreateOrderedEnumerable<TKey>(Func<TElement, TKey> keySelector, IComparer<TKey> comparer, bool descending)
                => descending
                ? _source.OrderByDescending(keySelector, comparer)
                : _source.OrderBy(keySelector, comparer);

            public IEnumerator<TElement> GetEnumerator()
                => _source.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator()
                => ((IEnumerable)_source).GetEnumerator();
        }
    }
}

// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Extensions.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.Expressions.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.NavigationExpansion.Visitors;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion
{
    public class NavigationExpansionExpression : Expression, IPrintable
    {
        private MethodInfo _queryableSelectMethodInfo
            = typeof(Queryable).GetMethods().Where(m => m.Name == nameof(Queryable.Select) && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Count() == 2).Single();

        private MethodInfo _enumerableSelectMethodInfo
            = typeof(Enumerable).GetMethods().Where(m => m.Name == nameof(Enumerable.Select) && m.GetParameters()[1].ParameterType.GetGenericArguments().Count() == 2).Single();

        private Type _returnType;

        public override ExpressionType NodeType => ExpressionType.Extension;
        public override Type Type => _returnType;
        public override bool CanReduce => true;
        public override Expression Reduce()
        {
            // TODO: this doesn't have to be a visitor, since we always have NEE on the top level
            var includeResult = ApplyIncludes();
            State = includeResult.state;
            var result = includeResult.operand;

            if (!State.ApplyPendingSelector
                && State.PendingCardinalityReducingOperator == null
                && State.MaterializeCollectionNavigation == null
                && State.PendingOrderings.Count == 0)
            {
                //// TODO: hack to workaround type discrepancy that can happen sometimes when rerwriting collection navigations
                //if (Operand.Type != _returnType)
                //{
                //    return Convert(Operand, _returnType);
                //}

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

                // we can't get body type using lambda.Body.Type because in some cases (SelectMany) we manually set the lambda type (IEnumerable<Entity>) where the body itself is IQueryable
                // TODO: this might be problem in other places!
                var pendingSelectorBodyType = pendingSelector.Type.GetGenericArguments()[1];

                var pendingSelectMathod = result.Type.IsGenericType && (result.Type.GetGenericTypeDefinition() == typeof(IEnumerable<>) || result.Type.GetGenericTypeDefinition() == typeof(IOrderedEnumerable<>))
                    ? _enumerableSelectMethodInfo.MakeGenericMethod(parameter.Type, pendingSelectorBodyType)
                    : _queryableSelectMethodInfo.MakeGenericMethod(parameter.Type, pendingSelectorBodyType);

                result = Call(pendingSelectMathod, result, pendingSelector);
                parameter = Parameter(result.Type.GetSequenceType());
            }

            if (State.PendingCardinalityReducingOperator != null)
            {
                var terminatingOperatorMethodInfo = State.PendingCardinalityReducingOperator.MakeGenericMethod(parameter.Type);

                result = Call(terminatingOperatorMethodInfo, result);
            }

            if (State.MaterializeCollectionNavigation != null)
            {
                result = Call(
                    NavigationExpansionHelpers.MaterializeCollectionNavigationMethodInfo.MakeGenericMethod(
                        State.MaterializeCollectionNavigation.ClrType,
                        State.MaterializeCollectionNavigation.GetTargetType().ClrType),
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
                    // TODO: how to handle properly?
                    return Convert(result, _returnType);
                }

            }

            return result;
        }

        private (Expression operand, NavigationExpansionExpressionState state) ApplyIncludes()
        {
            var includeFinder = new PendingIncludeFindingVisitor();
            includeFinder.Visit(State.PendingSelector);

            var includeRewriter = new PendingSelectorIncludeRewriter();
            var rewritten = (LambdaExpression)includeRewriter.Visit(State.PendingSelector);

            if (State.PendingSelector != rewritten)
            {
                State.PendingSelector = rewritten;
                State.ApplyPendingSelector = true;
            }

            if (includeFinder.PendingIncludes.Count > 0)
            {
                var result = (source: Operand, parameter: State.CurrentParameter);

                foreach (var pendingIncludeNode in includeFinder.PendingIncludes)
                {
                    if (pendingIncludeNode.Key.Navigation.IsCollection())
                    {
                        throw new InvalidOperationException("Collections should not be part of the navigation tree: " + pendingIncludeNode.Key.Navigation);
                    }

                    result = AddNavigationJoin(
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
                    pendingSelector = Expression.Lambda(pendingSelectorBody, result.parameter);
                }

                var newState = new NavigationExpansionExpressionState(
                    result.parameter,
                    State.SourceMappings,
                    pendingSelector,
                    applyPendingSelector: true,
                    State.PendingOrderings,
                    State.PendingIncludeChain,
                    State.PendingCardinalityReducingOperator,
                    State.CustomRootMappings,
                    State.MaterializeCollectionNavigation);

                return (operand: result.source, state: newState);
            }

            return (operand: Operand, state: State);
        }

        private (Expression source, ParameterExpression parameter) AddNavigationJoin(
            Expression sourceExpression,
            ParameterExpression parameterExpression,
            SourceMapping sourceMapping,
            NavigationTreeNode navigationTree,
            NavigationExpansionExpressionState state,
            List<INavigation> navigationPath,
            bool include)
        {
            var joinNeeded = include
                ? navigationTree.Included == NavigationTreeNodeIncludeMode.ReferencePending
                : navigationTree.ExpansionMode == NavigationTreeNodeExpansionMode.ReferencePending;
            if (joinNeeded)
            {
                // TODO: hack - if we wrapped collection around MaterializeCollectionNavigation during collection rewrite, unwrap that call when applying navigations on top
                if (sourceExpression is MethodCallExpression sourceMethodCall
                    && sourceMethodCall.Method.Name == "MaterializeCollectionNavigation")
                {
                    sourceExpression = sourceMethodCall.Arguments[1];
                }

                var navigation = navigationTree.Navigation;
                var sourceType = sourceExpression.Type.GetSequenceType();
                var navigationTargetEntityType = navigation.GetTargetType();

                var entityQueryable = NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(navigationTargetEntityType.ClrType);
                var resultType = typeof(TransparentIdentifier<,>).MakeGenericType(sourceType, navigationTargetEntityType.ClrType);

                var outerParameter = Expression.Parameter(sourceType, parameterExpression.Name);
                var outerKeySelectorParameter = outerParameter;
                var transparentIdentifierAccessorExpression = BuildTransparentIdentifierAccessorExpression(outerParameter, null, navigationTree.Parent.ToMapping);

                var outerKeySelectorBody = NavigationExpansionHelpers.CreateKeyAccessExpression(
                    transparentIdentifierAccessorExpression,
                    navigation.IsDependentToPrincipal()
                        ? navigation.ForeignKey.Properties
                        : navigation.ForeignKey.PrincipalKey.Properties,
                    addNullCheck: navigationTree.Parent != null && navigationTree.Parent.Optional);

                var innerKeySelectorParameterType = navigationTargetEntityType.ClrType;
                var innerKeySelectorParameter = Expression.Parameter(
                    innerKeySelectorParameterType,
                    parameterExpression.Name + "." + navigationTree.Navigation.Name);

                var innerKeySelectorBody = NavigationExpansionHelpers.CreateKeyAccessExpression(
                    innerKeySelectorParameter,
                    navigation.IsDependentToPrincipal()
                        ? navigation.ForeignKey.PrincipalKey.Properties
                        : navigation.ForeignKey.Properties);

                if (outerKeySelectorBody.Type.IsNullableType()
                    && !innerKeySelectorBody.Type.IsNullableType())
                {
                    innerKeySelectorBody = Expression.Convert(innerKeySelectorBody, outerKeySelectorBody.Type);
                }
                else if (innerKeySelectorBody.Type.IsNullableType()
                    && !outerKeySelectorBody.Type.IsNullableType())
                {
                    outerKeySelectorBody = Expression.Convert(outerKeySelectorBody, innerKeySelectorBody.Type);
                }

                var outerKeySelector = Expression.Lambda(
                    outerKeySelectorBody,
                    outerKeySelectorParameter);

                var innerKeySelector = Expression.Lambda(
                    innerKeySelectorBody,
                    innerKeySelectorParameter);

                var oldParameterExpression = parameterExpression;
                if (navigationTree.Optional)
                {
                    var groupingType = typeof(IEnumerable<>).MakeGenericType(navigationTargetEntityType.ClrType);
                    var groupJoinResultType = typeof(TransparentIdentifier<,>).MakeGenericType(sourceType, groupingType);

                    var groupJoinMethodInfo = LinqMethodHelpers.QueryableGroupJoinMethodInfo.MakeGenericMethod(
                        sourceType,
                        navigationTargetEntityType.ClrType,
                        outerKeySelector.Body.Type,
                        groupJoinResultType);

                    // TODO: massive hack!!!!
                    if (sourceExpression.Type.ToString().StartsWith("System.Collections.Generic.IEnumerable`1[")
                        || sourceExpression.Type.ToString().StartsWith("System.Linq.IOrderedEnumerable`1["))
                    {
                        groupJoinMethodInfo = LinqMethodHelpers.EnumerableGroupJoinMethodInfo.MakeGenericMethod(
                        sourceType,
                        navigationTargetEntityType.ClrType,
                        outerKeySelector.Body.Type,
                        groupJoinResultType);
                    }

                    var resultSelectorOuterParameterName = outerParameter.Name;
                    var resultSelectorOuterParameter = Expression.Parameter(sourceType, resultSelectorOuterParameterName);

                    var resultSelectorInnerParameterName = innerKeySelectorParameter.Name;
                    var resultSelectorInnerParameter = Expression.Parameter(groupingType, resultSelectorInnerParameterName);

                    var groupJoinResultTransparentIdentifierCtorInfo
                        = groupJoinResultType.GetTypeInfo().GetConstructors().Single();

                    var groupJoinResultSelector = Expression.Lambda(
                        Expression.New(groupJoinResultTransparentIdentifierCtorInfo, resultSelectorOuterParameter, resultSelectorInnerParameter),
                        resultSelectorOuterParameter,
                        resultSelectorInnerParameter);

                    var groupJoinMethodCall
                        = Expression.Call(
                            groupJoinMethodInfo,
                            sourceExpression,
                            entityQueryable,
                            outerKeySelector,
                            innerKeySelector,
                            groupJoinResultSelector);

                    var selectManyResultType = typeof(TransparentIdentifier<,>).MakeGenericType(groupJoinResultType, navigationTargetEntityType.ClrType);

                    var selectManyMethodInfo = LinqMethodHelpers.QueryableSelectManyWithResultOperatorMethodInfo.MakeGenericMethod(
                        groupJoinResultType,
                        navigationTargetEntityType.ClrType,
                        selectManyResultType);

                    // TODO: massive hack!!!!
                    if (groupJoinMethodCall.Type.ToString().StartsWith("System.Collections.Generic.IEnumerable`1[")
                        || groupJoinMethodCall.Type.ToString().StartsWith("System.Linq.IOrderedEnumerable`1["))
                    {
                        selectManyMethodInfo = LinqMethodHelpers.EnumerableSelectManyWithResultOperatorMethodInfo.MakeGenericMethod(
                            groupJoinResultType,
                            navigationTargetEntityType.ClrType,
                            selectManyResultType);
                    }

                    var defaultIfEmptyMethodInfo = LinqMethodHelpers.EnumerableDefaultIfEmptyMethodInfo.MakeGenericMethod(navigationTargetEntityType.ClrType);

                    var selectManyCollectionSelectorParameter = Expression.Parameter(groupJoinResultType);
                    var selectManyCollectionSelector = Expression.Lambda(
                        Expression.Call(
                            defaultIfEmptyMethodInfo,
                            Expression.Field(selectManyCollectionSelectorParameter, nameof(TransparentIdentifier<object, object>.Inner))),
                        selectManyCollectionSelectorParameter);

                    var selectManyResultTransparentIdentifierCtorInfo
                        = selectManyResultType.GetTypeInfo().GetConstructors().Single();

                    // TODO: dont reuse parameters here?
                    var selectManyResultSelector = Expression.Lambda(
                        Expression.New(selectManyResultTransparentIdentifierCtorInfo, selectManyCollectionSelectorParameter, innerKeySelectorParameter),
                        selectManyCollectionSelectorParameter,
                        innerKeySelectorParameter);

                    var selectManyMethodCall
                        = Expression.Call(selectManyMethodInfo,
                        groupJoinMethodCall,
                        selectManyCollectionSelector,
                        selectManyResultSelector);

                    sourceType = selectManyResultSelector.ReturnType;
                    sourceExpression = selectManyMethodCall;

                    var transparentIdentifierParameterName = resultSelectorInnerParameterName;
                    var transparentIdentifierParameter = Expression.Parameter(selectManyResultSelector.ReturnType, transparentIdentifierParameterName);
                    parameterExpression = transparentIdentifierParameter;
                }
                else
                {
                    var joinMethodInfo = LinqMethodHelpers.QueryableJoinMethodInfo.MakeGenericMethod(
                        sourceType,
                        navigationTargetEntityType.ClrType,
                        outerKeySelector.Body.Type,
                        resultType);

                    // TODO: massive hack!!!!
                    if (sourceExpression.Type.ToString().StartsWith("System.Collections.Generic.IEnumerable`1[")
                        || sourceExpression.Type.ToString().StartsWith("System.Linq.IOrderedEnumerable`1["))
                    {
                        joinMethodInfo = LinqMethodHelpers.EnumerableJoinMethodInfo.MakeGenericMethod(
                        sourceType,
                        navigationTargetEntityType.ClrType,
                        outerKeySelector.Body.Type,
                        resultType);
                    }

                    var resultSelectorOuterParameterName = outerParameter.Name;
                    var resultSelectorOuterParameter = Expression.Parameter(sourceType, resultSelectorOuterParameterName);

                    var resultSelectorInnerParameterName = innerKeySelectorParameter.Name;
                    var resultSelectorInnerParameter = Expression.Parameter(navigationTargetEntityType.ClrType, resultSelectorInnerParameterName);

                    var transparentIdentifierCtorInfo
                        = resultType.GetTypeInfo().GetConstructors().Single();

                    var resultSelector = Expression.Lambda(
                        Expression.New(transparentIdentifierCtorInfo, resultSelectorOuterParameter, resultSelectorInnerParameter),
                        resultSelectorOuterParameter,
                        resultSelectorInnerParameter);

                    var joinMethodCall = Expression.Call(
                        joinMethodInfo,
                        sourceExpression,
                        entityQueryable,
                        outerKeySelector,
                        innerKeySelector,
                        resultSelector);

                    sourceType = resultSelector.ReturnType;
                    sourceExpression = joinMethodCall;

                    var transparentIdentifierParameterName = resultSelectorInnerParameterName;
                    var transparentIdentifierParameter = Expression.Parameter(resultSelector.ReturnType, transparentIdentifierParameterName);
                    parameterExpression = transparentIdentifierParameter;
                }

                // remap navigation 'To' paths -> for this navigation prepend "Inner", for every other (already expanded) navigation prepend "Outer"
                navigationTree.ToMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Inner));
                foreach (var mapping in state.SourceMappings)
                {
                    // TODO: this is very hacky - combine those two enums
                    var nodes = include
                        ? mapping.NavigationTree.Flatten().Where(n => (n.Included == NavigationTreeNodeIncludeMode.ReferenceComplete || n.ExpansionMode == NavigationTreeNodeExpansionMode.ReferenceComplete) && n != navigationTree)
                        : mapping.NavigationTree.Flatten().Where(n => n.ExpansionMode == NavigationTreeNodeExpansionMode.ReferenceComplete && n != navigationTree);

                    foreach (var navigationTreeNode in nodes)
                    {
                        navigationTreeNode.ToMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
                        if (navigationTree.Optional)
                        {
                            navigationTreeNode.ToMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
                        }
                    }
                }

                foreach (var customRootMapping in state.CustomRootMappings)
                {
                    customRootMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
                    if (navigationTree.Optional)
                    {
                        customRootMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
                    }
                }

                if (include)
                {
                    navigationTree.Included = NavigationTreeNodeIncludeMode.ReferenceComplete;
                }
                else
                {
                    navigationTree.ExpansionMode = NavigationTreeNodeExpansionMode.ReferenceComplete;

                }
                navigationPath.Add(navigation);
            }
            else
            {
                navigationPath.Add(navigationTree.Navigation);
            }

            var result = (source: sourceExpression, parameter: parameterExpression);
            foreach (var child in navigationTree.Children.Where(n => !n.Navigation.IsCollection()))
            {
                result = AddNavigationJoin(
                    result.source,
                    result.parameter,
                    sourceMapping,
                    child,
                    state,
                    navigationPath.ToList(),
                    include);
            }

            return result;
        }

        // TODO: DRY
        private Expression BuildTransparentIdentifierAccessorExpression(Expression source, List<string> initialPath, List<string> accessorPath)
        {
            var result = source;

            var fullPath = initialPath != null
                ? initialPath.Concat(accessorPath).ToList()
                : accessorPath;

            if (fullPath != null)
            {
                foreach (var accessorPathElement in fullPath)
                {
                    result = Expression.PropertyOrField(result, accessorPathElement);
                }
            }

            return result;
        }

        public Expression Operand { get; }

        public NavigationExpansionExpressionState State { get; private set; }

        public NavigationExpansionExpression(
            Expression operand,
            NavigationExpansionExpressionState state,
            Type returnType)
        {
            Operand = operand;
            State = state;
            _returnType = returnType;
        }

        public void Print([NotNull] ExpressionPrinter expressionPrinter)
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

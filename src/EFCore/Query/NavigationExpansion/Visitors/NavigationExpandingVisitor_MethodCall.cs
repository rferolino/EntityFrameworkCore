// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Extensions.Internal;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion.Visitors
{
    public partial class NavigationExpandingVisitor : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            switch (methodCallExpression.Method.Name)
            {
                case nameof(Queryable.Where):
                    return ProcessWhere(methodCallExpression);

                case nameof(Queryable.Select):
                    return ProcessSelect(methodCallExpression);

                case nameof(Queryable.OrderBy):
                case nameof(Queryable.OrderByDescending):
                    return ProcessOrderBy(methodCallExpression);

                case nameof(Queryable.ThenBy):
                case nameof(Queryable.ThenByDescending):
                    return ProcessThenByBy(methodCallExpression);

                case nameof(Queryable.Join):
                    return ProcessJoin(methodCallExpression);

                case nameof(Queryable.GroupJoin):
                    return ProcessGroupJoin(methodCallExpression);

                case nameof(Queryable.SelectMany):
                    return ProcessSelectMany(methodCallExpression);

                case nameof(Queryable.GroupBy):
                    return ProcessGroupBy(methodCallExpression);

                case nameof(Queryable.All):
                    return ProcessAll(methodCallExpression);

                case nameof(Queryable.Any):
                    return ProcessAny(methodCallExpression);

                case nameof(Queryable.Average):
                    return ProcessAverage(methodCallExpression);

                case nameof(Queryable.Count):
                    return ProcessCount(methodCallExpression);

                case nameof(Queryable.Distinct):
                    return ProcessDistinct(methodCallExpression);

                case nameof(Queryable.DefaultIfEmpty):
                    return ProcessDefaultIfEmpty(methodCallExpression);

                case "AsTracking":
                case "AsNoTracking":
                    return ProcessBasicTerminatingOperation(methodCallExpression);

                case nameof(Queryable.First):
                case nameof(Queryable.FirstOrDefault):
                case nameof(Queryable.Single):
                case nameof(Queryable.SingleOrDefault):
                    return ProcessCardinalityReducingOperation(methodCallExpression);

                case nameof(Queryable.OfType):
                    return ProcessOfType(methodCallExpression);

                case nameof(Queryable.Skip):
                case nameof(Queryable.Take):
                    return ProcessSkipTake(methodCallExpression);

                case "Include":
                case "ThenInclude":
                    return ProcessInclude(methodCallExpression);

                case "MaterializeCollectionNavigation":
                    return ProcessMaterializeCollectionNavigation(methodCallExpression);
                    //var newArgument = (NavigationExpansionExpression)Visit(methodCallExpression.Arguments[1]);
                    //return new NavigationExpansionExpression(newArgument, newArgument.State, methodCallExpression.Type);

                default:
                    return base.VisitMethodCall(methodCallExpression);
            }
        }

        private NavigationExpansionExpression VisitSourceExpression(Expression sourceExpression)
        {
            var result = Visit(sourceExpression);
            if (result is NavigationExpansionRootExpression navigationExpansionRootExpression)
            {
                result = navigationExpansionRootExpression.Unwrap();
            }

            if (result is NavigationExpansionExpression navigationExpansionExpression)
            {
                return navigationExpansionExpression;
            }

            var currentParameter = Expression.Parameter(result.Type.GetSequenceType());
            var customRootMapping = new List<string>();

            var state = new NavigationExpansionExpressionState(
                currentParameter,
                new List<SourceMapping>(),
                Expression.Lambda(new CustomRootExpression(currentParameter, customRootMapping, currentParameter.Type), currentParameter),
                applyPendingSelector: false,
                new List<(MethodInfo method, LambdaExpression keySelector)>(),
                pendingIncludeChain: null,
                pendingCardinalityReducingOperator: null,
                new List<List<string>> { customRootMapping },
                materializeCollectionNavigation: null);

            return new NavigationExpansionExpression(result, state, result.Type);
        }

        private Expression ProcessMaterializeCollectionNavigation(MethodCallExpression methodCallExpression)
        {
            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            var navigation = (INavigation)((ConstantExpression)methodCallExpression.Arguments[1]).Value;
            source.State.MaterializeCollectionNavigation = navigation;

            var result = new NavigationExpansionExpression(
                source.Operand,
                source.State,
                methodCallExpression.Type);

            return result;
        }

        private NavigationExpansionExpressionState AdjustState(
            NavigationExpansionExpressionState state,
            NavigationExpansionExpression navigationExpansionExpression)
        {
            var currentParameter = state.CurrentParameter;
            state = navigationExpansionExpression.State;

            if (state.CurrentParameter.Name == null
                && state.CurrentParameter.Name != currentParameter.Name)
            {
                AdjustCurrentParameterName(state, currentParameter.Name);
            }

            return state;
        }

        private void AdjustCurrentParameterName(
            NavigationExpansionExpressionState state,
            string newParameterName)
        {
            //if (state.CurrentParameter.Name != null || newParameterName == null || newParameterName == state.CurrentParameter.Name)
            //{
            //    return;
            //}

            if (state.CurrentParameter.Name == null && newParameterName != null)
            {
                var newParameter = Expression.Parameter(state.CurrentParameter.Type, newParameterName);
                state.PendingSelector = (LambdaExpression)new ExpressionReplacingVisitor(state.CurrentParameter, newParameter).Visit(state.PendingSelector);
                state.CurrentParameter = newParameter;
            }
        }

        private Expression ProcessWhere(MethodCallExpression methodCallExpression)
        {
            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            var predicate = methodCallExpression.Arguments[1].UnwrapQuote();
            AdjustCurrentParameterName(source.State, predicate.Parameters[0].Name);

            var appliedNavigationsResult = FindAndApplyNavigations(source.Operand, predicate, source.State);
            var newPredicateBody = new NavigationPropertyUnbindingVisitor(appliedNavigationsResult.state.CurrentParameter).Visit(appliedNavigationsResult.lambdaBody);
            var newPredicateLambda = Expression.Lambda(newPredicateBody, appliedNavigationsResult.state.CurrentParameter);
            var appliedOrderingsResult = ApplyPendingOrderings(appliedNavigationsResult.source, appliedNavigationsResult.state);

            var newMethodInfo = methodCallExpression.Method.GetGenericMethodDefinition().MakeGenericMethod(appliedOrderingsResult.state.CurrentParameter.Type);
            var rewritten = Expression.Call(newMethodInfo, appliedOrderingsResult.source, newPredicateLambda);

            return new NavigationExpansionExpression(
                rewritten,
                appliedOrderingsResult.state,
                methodCallExpression.Type);

            //var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            //var predicate = methodCallExpression.Arguments[1].UnwrapQuote();
            //var state = new NavigationExpansionExpressionState(predicate.Parameters[0]);

            //if (source is NavigationExpansionExpression navigationExpansionExpression)
            //{
            //    source = navigationExpansionExpression.Operand;
            //    state = AdjustState(state, navigationExpansionExpression);
            //}

            //var result = FindAndApplyNavigations(source, predicate, state);
            //var newPredicateBody = new NavigationPropertyUnbindingVisitor(result.state.CurrentParameter).Visit(result.lambdaBody);

            //source = ApplyPendingOrderings(result.source, result.state);

            //var newMethodInfo = methodCallExpression.Method.GetGenericMethodDefinition().MakeGenericMethod(result.state.CurrentParameter.Type);
            //var rewritten = Expression.Call(
            //    newMethodInfo,
            //    source,
            //    Expression.Lambda(
            //        newPredicateBody,
            //        result.state.CurrentParameter));

            //return new NavigationExpansionExpression(
            //    rewritten,
            //    result.state,
            //    methodCallExpression.Type);
        }

        private Expression ProcessSelect(MethodCallExpression methodCallExpression)
        {
            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            var selector = methodCallExpression.Arguments[1].UnwrapQuote();
            AdjustCurrentParameterName(source.State, selector.Parameters[0].Name);

            return ProcessSelectCore(source.Operand, source.State, selector, methodCallExpression.Type);
        }

        private Expression ProcessSelectCore(Expression source, NavigationExpansionExpressionState state, LambdaExpression selector, Type resultType)
        {
            var appliedNavigationsResult = FindAndApplyNavigations(source, selector, state);
            appliedNavigationsResult.state.PendingSelector = Expression.Lambda(appliedNavigationsResult.lambdaBody, appliedNavigationsResult.state.CurrentParameter);

            // TODO: unless it's identity projection
            appliedNavigationsResult.state.ApplyPendingSelector = true;

            var appliedOrderingsResult = ApplyPendingOrderings(appliedNavigationsResult.source, appliedNavigationsResult.state);

            // TODO: should we take this into account in other places? (i.e. that result type can be non-collection)
            var resultElementType = resultType.TryGetSequenceType();
            if (resultElementType != null)
            {
                if (resultElementType != appliedOrderingsResult.state.PendingSelector.Body.Type)
                {
                    resultType = resultType.GetGenericTypeDefinition().MakeGenericType(appliedOrderingsResult.state.PendingSelector.Body.Type);
                }
            }
            else
            {
                resultType = appliedOrderingsResult.state.PendingSelector.Body.Type;
            }

            return new NavigationExpansionExpression(
                appliedOrderingsResult.source,
                appliedOrderingsResult.state,
                resultType);
        }

        private Expression ProcessOrderBy(MethodCallExpression methodCallExpression)
        {
            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            var keySelector = methodCallExpression.Arguments[1].UnwrapQuote();
            AdjustCurrentParameterName(source.State, keySelector.Parameters[0].Name);

            var appliedNavigationsResult = FindAndApplyNavigations(source.Operand, keySelector, source.State);
            var pendingOrdering = (method: methodCallExpression.Method.GetGenericMethodDefinition(), keySelector: Expression.Lambda(appliedNavigationsResult.lambdaBody, appliedNavigationsResult.state.CurrentParameter));
            var appliedOrderingsResult = ApplyPendingOrderings(appliedNavigationsResult.source, appliedNavigationsResult.state);

            appliedOrderingsResult.state.PendingOrderings.Add(pendingOrdering);

            return new NavigationExpansionExpression(
                appliedOrderingsResult.source,
                appliedOrderingsResult.state,
                methodCallExpression.Type);
        }

        private Expression ProcessThenByBy(MethodCallExpression methodCallExpression)
        {
            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            var keySelector = methodCallExpression.Arguments[1].UnwrapQuote();
            AdjustCurrentParameterName(source.State, keySelector.Parameters[0].Name);

            var appliedNavigationsResult = FindAndApplyNavigations(source.Operand, keySelector, source.State);

            var pendingOrdering = (method: methodCallExpression.Method.GetGenericMethodDefinition(), keySelector: Expression.Lambda(appliedNavigationsResult.lambdaBody, appliedNavigationsResult.state.CurrentParameter));
            appliedNavigationsResult.state.PendingOrderings.Add(pendingOrdering);

            return new NavigationExpansionExpression(
                appliedNavigationsResult.source,
                appliedNavigationsResult.state,
                methodCallExpression.Type);
        }

        private Expression ProcessSelectMany(MethodCallExpression methodCallExpression)
        {
            var outerSourceNee = VisitSourceExpression(methodCallExpression.Arguments[0]);

            //var outerSource = VisitSourceExpression(methodCallExpression.Arguments[0]);
            //var outerState = new NavigationExpansionExpressionState(Expression.Parameter(methodCallExpression.Method.GetGenericArguments()[0]));

            //if (outerSource is NavigationExpansionExpression outerNavigationExpansionExpression)
            //{
            //    outerSource = outerNavigationExpansionExpression.Operand;
            //    outerState = AdjustState(outerState, outerNavigationExpansionExpression);
            //}

            var collectionSelector = methodCallExpression.Arguments[1].UnwrapQuote();
            AdjustCurrentParameterName(outerSourceNee.State, collectionSelector.Parameters[0].Name);

            var applyNavigsationsResult = FindAndApplyNavigations(outerSourceNee.Operand, collectionSelector, outerSourceNee.State);
            var applyOrderingsResult = ApplyPendingOrderings(applyNavigsationsResult.source, applyNavigsationsResult.state);

            var outerSource = applyOrderingsResult.source;
            var outerState = applyOrderingsResult.state;

            var collectionSelectorNavigationExpansionExpression = applyNavigsationsResult.lambdaBody as NavigationExpansionExpression
                ?? (applyNavigsationsResult.lambdaBody as NavigationExpansionRootExpression)?.Unwrap() as NavigationExpansionExpression;

            if (collectionSelectorNavigationExpansionExpression != null)
            //if (collectionSelectorResult.lambdaBody is NavigationExpansionExpression collectionSelectorNavigationExpansionExpression)
            {
                var collectionSelectorState = collectionSelectorNavigationExpansionExpression.State;
                var collectionSelectorLambdaBody = collectionSelectorNavigationExpansionExpression.Operand;

                // in case collection selector is a "naked" collection navigation, we need to remove MaterializeCollectionNavigation
                // it's not needed for SelectMany collection selectors as they are not directly projected
                collectionSelectorState.MaterializeCollectionNavigation = null;

                if (methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.QueryableSelectManyWithResultOperatorMethodInfo))
                {
                    // TODO: && or || ??
                    if (outerState.CurrentParameter.Name == null
                        && outerState.CurrentParameter.Name != methodCallExpression.Arguments[2].UnwrapQuote().Parameters[0].Name)
                    {
                        var newOuterParameter = Expression.Parameter(outerState.CurrentParameter.Type, methodCallExpression.Arguments[2].UnwrapQuote().Parameters[0].Name);
                        outerState.PendingSelector = (LambdaExpression)new ExpressionReplacingVisitor(outerState.CurrentParameter, newOuterParameter).Visit(outerState.PendingSelector);
                        collectionSelectorLambdaBody = new ExpressionReplacingVisitor(outerState.CurrentParameter, newOuterParameter).Visit(collectionSelectorLambdaBody);
                        outerState.CurrentParameter = newOuterParameter;
                    }

                    if (collectionSelectorState.CurrentParameter.Name == null
                        && collectionSelectorState.CurrentParameter.Name != methodCallExpression.Arguments[2].UnwrapQuote().Parameters[1].Name)
                    {
                        var newInnerParameter = Expression.Parameter(collectionSelectorState.CurrentParameter.Type, methodCallExpression.Arguments[2].UnwrapQuote().Parameters[1].Name);
                        collectionSelectorState.PendingSelector = (LambdaExpression)new ExpressionReplacingVisitor(collectionSelectorState.CurrentParameter, newInnerParameter).Visit(collectionSelectorState.PendingSelector);
                        collectionSelectorState.CurrentParameter = newInnerParameter;
                    }
                }

                if (methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.QueryableSelectManyWithResultOperatorMethodInfo)
                    && (collectionSelectorState.CurrentParameter.Name == null
                        || collectionSelectorState.CurrentParameter.Name != methodCallExpression.Arguments[2].UnwrapQuote().Parameters[1].Name))
                {
                    // TODO: should we rename the second parameter according to the second parameter of the result selector instead?
                    var newParameter = Expression.Parameter(collectionSelectorState.CurrentParameter.Type, methodCallExpression.Arguments[2].UnwrapQuote().Parameters[1].Name);
                    collectionSelectorState.PendingSelector = (LambdaExpression)new ExpressionReplacingVisitor(collectionSelectorState.CurrentParameter, newParameter).Visit(collectionSelectorState.PendingSelector);
                    collectionSelectorState.CurrentParameter = newParameter;
                }

                // in case collection selector body is IQueryable, we need to adjust the type to IEnumerable, to match the SelectMany signature
                // therefore the delegate type is specified explicitly
                var collectionSelectorLambdaType = typeof(Func<,>).MakeGenericType(
                    outerState.CurrentParameter.Type,
                    typeof(IEnumerable<>).MakeGenericType(collectionSelectorNavigationExpansionExpression.State.CurrentParameter.Type));

                var newCollectionSelectorLambda = Expression.Lambda(
                    collectionSelectorLambdaType,
                    collectionSelectorLambdaBody,
                    outerState.CurrentParameter);

                newCollectionSelectorLambda = (LambdaExpression)new NavigationPropertyUnbindingVisitor(outerState.CurrentParameter).Visit(newCollectionSelectorLambda);

                if (methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.QueryableSelectManyMethodInfo))
                {
                    return BuildSelectManyWithoutResultOperatorMethodCall(methodCallExpression, outerSource, outerState, newCollectionSelectorLambda, collectionSelectorState);
                }

                var resultSelector = methodCallExpression.Arguments[2].UnwrapQuote();
                var resultSelectorRemap = RemapTwoArgumentResultSelector(resultSelector, outerState, collectionSelectorNavigationExpansionExpression.State);

                var newMethodInfo = methodCallExpression.Method.GetGenericMethodDefinition().MakeGenericMethod(
                    outerState.CurrentParameter.Type,
                    collectionSelectorState.CurrentParameter.Type,
                    resultSelectorRemap.lambda.Body.Type);

                var rewritten = Expression.Call(
                    newMethodInfo,
                    outerSource,
                    newCollectionSelectorLambda,
                    resultSelectorRemap.lambda);

                // temporarily change selector to ti => ti for purpose of finding & expanding navigations in the pending selector lambda itself
                var pendingSelector = resultSelectorRemap.state.PendingSelector;
                resultSelectorRemap.state.PendingSelector = Expression.Lambda(resultSelectorRemap.state.PendingSelector.Parameters[0], resultSelectorRemap.state.PendingSelector.Parameters[0]);
                var result = FindAndApplyNavigations(rewritten, pendingSelector, resultSelectorRemap.state);
                result.state.PendingSelector = Expression.Lambda(result.lambdaBody, result.state.CurrentParameter);

                return new NavigationExpansionExpression(
                    result.source,
                    result.state,
                    methodCallExpression.Type);
            }

            throw new InvalidOperationException("collection selector was not NavigationExpansionExpression");
        }

        private Expression BuildSelectManyWithoutResultOperatorMethodCall(
            MethodCallExpression methodCallExpression,
            Expression outerSource,
            NavigationExpansionExpressionState outerState,
            LambdaExpression newCollectionSelectorLambda,
            NavigationExpansionExpressionState collectionSelectorState)
        {
            var newMethodInfo = methodCallExpression.Method.GetGenericMethodDefinition().MakeGenericMethod(
                outerState.CurrentParameter.Type,
                collectionSelectorState.CurrentParameter.Type);

            var rewritten = Expression.Call(
                newMethodInfo,
                outerSource,
                newCollectionSelectorLambda);

            return new NavigationExpansionExpression(
                rewritten,
                collectionSelectorState,
                methodCallExpression.Type);
        }

        private Expression ProcessJoin(MethodCallExpression methodCallExpression)
        {
            // TODO: move this to the big switch/case - this is for the string.Join case which would go here since it's matched by name atm
            if (!methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.QueryableJoinMethodInfo))
            {
                return base.VisitMethodCall(methodCallExpression);
            }

            var outerSource = VisitSourceExpression(methodCallExpression.Arguments[0]);
            var innerSource = VisitSourceExpression(methodCallExpression.Arguments[1]);

            var outerKeySelector = methodCallExpression.Arguments[2].UnwrapQuote();
            var innerKeySelector = methodCallExpression.Arguments[3].UnwrapQuote();
            var resultSelector = methodCallExpression.Arguments[4].UnwrapQuote();

            AdjustCurrentParameterName(outerSource.State, outerKeySelector.Parameters[0].Name);
            AdjustCurrentParameterName(innerSource.State, innerKeySelector.Parameters[0].Name);

            var outerApplyNavigationsResult = FindAndApplyNavigations(outerSource.Operand, outerKeySelector, outerSource.State);
            var innerApplyNavigationsResult = FindAndApplyNavigations(innerSource.Operand, innerKeySelector, innerSource.State);

            var newOuterKeySelectorBody = new NavigationPropertyUnbindingVisitor(outerApplyNavigationsResult.state.CurrentParameter).Visit(outerApplyNavigationsResult.lambdaBody);
            var newInnerKeySelectorBody = new NavigationPropertyUnbindingVisitor(innerApplyNavigationsResult.state.CurrentParameter).Visit(innerApplyNavigationsResult.lambdaBody);

            var outerApplyOrderingsResult = ApplyPendingOrderings(outerApplyNavigationsResult.source, outerApplyNavigationsResult.state);
            var innerApplyOrderingsResult = ApplyPendingOrderings(innerApplyNavigationsResult.source, innerApplyNavigationsResult.state);

            var resultSelectorRemap = RemapTwoArgumentResultSelector(resultSelector, outerApplyOrderingsResult.state, innerApplyOrderingsResult.state);

            var newMethodInfo = methodCallExpression.Method.GetGenericMethodDefinition().MakeGenericMethod(
                outerApplyOrderingsResult.state.CurrentParameter.Type,
                innerApplyOrderingsResult.state.CurrentParameter.Type,
                outerApplyNavigationsResult.lambdaBody.Type,
                resultSelectorRemap.lambda.Body.Type);

            var rewritten = Expression.Call(
                newMethodInfo,
                outerApplyOrderingsResult.source,
                innerApplyOrderingsResult.source,
                Expression.Lambda(newOuterKeySelectorBody, outerApplyOrderingsResult.state.CurrentParameter),
                Expression.Lambda(newInnerKeySelectorBody, innerApplyOrderingsResult.state.CurrentParameter),
                Expression.Lambda(resultSelectorRemap.lambda.Body, outerApplyOrderingsResult.state.CurrentParameter, innerApplyOrderingsResult.state.CurrentParameter));

            // temporarily change selector to ti => ti for purpose of finding & expanding navigations in the pending selector lambda itself
            var pendingSelector = resultSelectorRemap.state.PendingSelector;
            resultSelectorRemap.state.PendingSelector = Expression.Lambda(resultSelectorRemap.state.PendingSelector.Parameters[0], resultSelectorRemap.state.PendingSelector.Parameters[0]);
            var result = FindAndApplyNavigations(rewritten, pendingSelector, resultSelectorRemap.state);
            result.state.PendingSelector = Expression.Lambda(result.lambdaBody, result.state.CurrentParameter);

            return new NavigationExpansionExpression(
                result.source,
                result.state,
                methodCallExpression.Type);
        }

        private Expression ProcessGroupJoin(MethodCallExpression methodCallExpression)
        {
            var outerSource = VisitSourceExpression(methodCallExpression.Arguments[0]);
            var innerSource = VisitSourceExpression(methodCallExpression.Arguments[1]);

            var outerKeySelector = methodCallExpression.Arguments[2].UnwrapQuote();
            var innerKeySelector = methodCallExpression.Arguments[3].UnwrapQuote();
            var resultSelector = methodCallExpression.Arguments[4].UnwrapQuote();

            AdjustCurrentParameterName(outerSource.State, outerKeySelector.Parameters[0].Name);
            AdjustCurrentParameterName(innerSource.State, innerKeySelector.Parameters[0].Name);

            var outerApplyNavigationsResult = FindAndApplyNavigations(outerSource.Operand, outerKeySelector, outerSource.State);
            var innerApplyNavigationsResult = FindAndApplyNavigations(innerSource.Operand, innerKeySelector, innerSource.State);

            var newOuterKeySelectorBody = new NavigationPropertyUnbindingVisitor(outerApplyNavigationsResult.state.CurrentParameter).Visit(outerApplyNavigationsResult.lambdaBody);
            var newInnerKeySelectorBody = new NavigationPropertyUnbindingVisitor(innerApplyNavigationsResult.state.CurrentParameter).Visit(innerApplyNavigationsResult.lambdaBody);

            var outerApplyOrderingsResult = ApplyPendingOrderings(outerApplyNavigationsResult.source, outerApplyNavigationsResult.state);
            var innerApplyOrderingsResult = ApplyPendingOrderings(innerApplyNavigationsResult.source, innerApplyNavigationsResult.state);

            //-----------------------------------------------------------------------------------------------------------------------------------------------------

            var resultSelectorBody = resultSelector.Body;
            var remappedResultSelector = ExpressionExtensions.CombineAndRemapLambdas(outerApplyOrderingsResult.state.PendingSelector, resultSelector, resultSelector.Parameters[0]);

            var groupingParameter = resultSelector.Parameters[1];
            var newGroupingParameter = Expression.Parameter(typeof(IEnumerable<>).MakeGenericType(innerApplyOrderingsResult.state.CurrentParameter.Type), "new_" + groupingParameter.Name);

            var groupingMapping = new List<string> { nameof(TransparentIdentifierGJ<object, object>.Inner) };

            // TODO: !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!

            // need to manipulate the state, not simply copy it over to the grouping
            // prolly generate completely new tree - mappings for the grouping should have the Inner added to them,
            // basically those should be 2 different mappings

            //fix that stuff!!!

            var newGrouping = new NavigationExpansionExpression(newGroupingParameter, innerApplyOrderingsResult.state, groupingParameter.Type);

            //var newGrouping = new CustomRootExpression2(
            //        new NavigationExpansionExpression(newGroupingParameter, innerState, groupingParameter.Type),
            //        groupingMapping,
            //        groupingParameter.Type);

            var remappedResultSelectorBody = new ExpressionReplacingVisitor(
                groupingParameter,
                new NavigationExpansionRootExpression(newGrouping, groupingMapping, groupingParameter.Type)).Visit(remappedResultSelector.Body);
            //remappedResultSelector = Expression.Lambda(remappedResultSelectorBody, outerState.CurrentParameter, newGroupingParameter);

            foreach (var outerCustomRootMapping in outerApplyOrderingsResult.state.CustomRootMappings)
            {
                outerCustomRootMapping.Insert(0, nameof(TransparentIdentifierGJ<object, object>.Outer));
            }

            foreach (var outerSourceMapping in outerApplyOrderingsResult.state.SourceMappings)
            {
                foreach (var navigationTreeNode in outerSourceMapping.NavigationTree.Flatten().Where(n => n.ExpansionMode == NavigationTreeNodeExpansionMode.Complete))
                {
                    navigationTreeNode.ToMapping.Insert(0, nameof(TransparentIdentifierGJ<object, object>.Outer));
                    foreach (var fromMapping in navigationTreeNode.FromMappings)
                    {
                        fromMapping.Insert(0, nameof(TransparentIdentifierGJ<object, object>.Outer));
                    }
                }
            }

            //var nestedExpansionMappings = outerState.NestedExpansionMappings.ToList();
            //nestedExpansionMappings.Add(new NestedExpansionMapping(groupingMapping, newGrouping));


            //foreach (var innerCustomRootMapping in innerState.CustomRootMappings)
            //{
            //    innerCustomRootMapping.Insert(0, nameof(TransparentIdentifierGJ<object, object>.InnerGJ));
            //}


            // TODO: hack !!!!!!!!!!!!!!!!!!!!!!!!!!!!!! <- we prolly need this but commenting for now to see if maybe it works like this by chance


            //foreach (var innerSourceMapping in innerState.SourceMappings)
            //{
            //    foreach (var navigationTreeNode in innerSourceMapping.NavigationTree.Flatten().Where(n => n.Expanded))
            //    {
            //        navigationTreeNode.ToMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Inner));
            //        foreach (var fromMapping in navigationTreeNode.FromMappings)
            //        {
            //            fromMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Inner));
            //        }
            //    }
            //}

            var resultType = typeof(TransparentIdentifierGJ<,>).MakeGenericType(outerApplyOrderingsResult.state.CurrentParameter.Type, newGroupingParameter.Type);
            var transparentIdentifierCtorInfo = resultType.GetTypeInfo().GetConstructors().Single();
            var transparentIdentifierParameter = Expression.Parameter(resultType, "groupjoin");

            var newPendingSelectorBody = new ExpressionReplacingVisitor(outerApplyOrderingsResult.state.CurrentParameter, transparentIdentifierParameter).Visit(remappedResultSelectorBody);
            newPendingSelectorBody = new ExpressionReplacingVisitor(newGroupingParameter, transparentIdentifierParameter).Visit(newPendingSelectorBody);

            var newState = new NavigationExpansionExpressionState(
                transparentIdentifierParameter,
                outerApplyOrderingsResult.state.SourceMappings.ToList(),
                Expression.Lambda(newPendingSelectorBody, transparentIdentifierParameter),
                applyPendingSelector: true,
                outerApplyOrderingsResult.state.PendingOrderings,
                outerApplyOrderingsResult.state.PendingIncludeChain,
                outerApplyOrderingsResult.state.PendingCardinalityReducingOperator, // TODO: incorrect?
                outerApplyOrderingsResult.state.CustomRootMappings,
                materializeCollectionNavigation: null
                /*nestedExpansionMappings*/);

            var lambda = Expression.Lambda(
                Expression.New(transparentIdentifierCtorInfo, outerApplyOrderingsResult.state.CurrentParameter, newGroupingParameter),
                outerApplyOrderingsResult.state.CurrentParameter,
                newGroupingParameter);

            var newMethodInfo = methodCallExpression.Method.GetGenericMethodDefinition().MakeGenericMethod(
                outerApplyOrderingsResult.state.CurrentParameter.Type,
                innerApplyOrderingsResult.state.CurrentParameter.Type,
                outerApplyNavigationsResult.lambdaBody.Type,
                lambda.Body.Type);

            var rewritten = Expression.Call(
                newMethodInfo,
                outerApplyOrderingsResult.source,
                innerApplyOrderingsResult.source,
                Expression.Lambda(newOuterKeySelectorBody, outerApplyOrderingsResult.state.CurrentParameter),
                Expression.Lambda(newInnerKeySelectorBody, innerApplyOrderingsResult.state.CurrentParameter),
                lambda);

            // TODO: expand navigations in the result selector of the GroupJoin!!!

            return new NavigationExpansionExpression(
                rewritten,
                newState,
                methodCallExpression.Type);
        }

        private Expression ProcessGroupBy(MethodCallExpression methodCallExpression)
        {
            return methodCallExpression;
        }

        private bool IsQueryable(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>))
            {
                return true;
            }

            return type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryable<>));
        }

        private Expression ProcessAll(MethodCallExpression methodCallExpression)
        {
            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            var predicate = methodCallExpression.Arguments[1].UnwrapQuote();
            AdjustCurrentParameterName(source.State, predicate.Parameters[0].Name);

            var applyNavigationsResult = FindAndApplyNavigations(source.Operand, predicate, source.State);
            var newPredicateBody = new NavigationPropertyUnbindingVisitor(applyNavigationsResult.state.CurrentParameter).Visit(applyNavigationsResult.lambdaBody);
            var applyOrderingsResult = ApplyPendingOrderings(applyNavigationsResult.source, applyNavigationsResult.state);

            var newMethodInfo = methodCallExpression.Method.GetGenericMethodDefinition().MakeGenericMethod(applyOrderingsResult.state.CurrentParameter.Type);
            var rewritten = Expression.Call(
                newMethodInfo,
                applyOrderingsResult.source,
                Expression.Lambda(
                    newPredicateBody,
                    applyOrderingsResult.state.CurrentParameter));

            return rewritten;
        }

        private Expression ProcessAny(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.QueryableAnyPredicateMethodInfo))
            {
                return ProcessAny(SimplifyPredicateMethod(methodCallExpression, queryable: true));
            }

            if (methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.EnumerableAnyPredicateMethodInfo))
            {
                return ProcessAny(SimplifyPredicateMethod(methodCallExpression, queryable: false));
            }

            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);

            return methodCallExpression.Update(methodCallExpression.Object, new[] { source });
        }

        private Expression ProcessAverage(MethodCallExpression methodCallExpression)
        {
            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            if (methodCallExpression.Arguments.Count == 2)
            {
                var selector = methodCallExpression.Arguments[1].UnwrapQuote();
                AdjustCurrentParameterName(source.State, selector.Parameters[0].Name);
                var applyNavigationsResult = FindAndApplyNavigations(source.Operand, selector, source.State);
                var newSelectorBody = new NavigationPropertyUnbindingVisitor(applyNavigationsResult.state.CurrentParameter).Visit(applyNavigationsResult.lambdaBody);
                var newSelector = Expression.Lambda(newSelectorBody, applyNavigationsResult.state.CurrentParameter);

                var applyOrderingsResult = ApplyPendingOrderings(source.Operand, source.State);
                var newMethod = methodCallExpression.Method.GetGenericMethodDefinition().MakeGenericMethod(applyNavigationsResult.state.CurrentParameter.Type);

                return Expression.Call(newMethod, applyOrderingsResult.source, newSelector);
            }

            return methodCallExpression.Update(methodCallExpression.Object, new[] { source });
        }

        private Expression ProcessCount(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.QueryableCountPredicateMethodInfo))
            {
                return ProcessCount(SimplifyPredicateMethod(methodCallExpression, queryable: true));
            }

            if (methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.EnumerableCountPredicateMethodInfo))
            {
                return ProcessCount(SimplifyPredicateMethod(methodCallExpression, queryable: false));
            }

            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);

            return methodCallExpression.Update(methodCallExpression.Object, new[] { source });
        }

        private Expression ProcessDistinct(MethodCallExpression methodCallExpression)
        {
            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            var preProcessResult = PreProcessTerminatingOperation(source);
            var rewritten = methodCallExpression.Update(methodCallExpression.Object, new[] { preProcessResult.source });

            return new NavigationExpansionExpression(rewritten, preProcessResult.state, methodCallExpression.Type);
        }

        private Expression ProcessDefaultIfEmpty(MethodCallExpression methodCallExpression)
        {
            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            foreach (var sourceMapping in source.State.SourceMappings)
            {
                sourceMapping.NavigationTree.MakeOptional();
            }

            // TODO: clean this up, i.e. in top level switch statement pick method based on method info, not only the name
            if (methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.QueryableDefaultIfEmptyWithDefaultValue))
            {
                var preProcessResult = PreProcessTerminatingOperation(source);
                var rewritten = methodCallExpression.Update(methodCallExpression.Object, new[] { preProcessResult.source });

                return new NavigationExpansionExpression(rewritten, preProcessResult.state, methodCallExpression.Type);
            }
            else
            {
                var newMethodInfo = methodCallExpression.Method.GetGenericMethodDefinition().MakeGenericMethod(source.State.CurrentParameter.Type);
                var rewritten = Expression.Call(newMethodInfo, source.Operand);

                return new NavigationExpansionExpression(rewritten, source.State, methodCallExpression.Type);
            }
        }

        private Expression ProcessOfType(MethodCallExpression methodCallExpression)
        {
            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            var preProcessResult = PreProcessTerminatingOperation(source);

            var newEntityType = _model.FindEntityType(methodCallExpression.Method.GetGenericArguments()[0]);

            // TODO: possible small optimization - only apply this if newEntityType is different than the old one
            if (newEntityType != null)
            {
                var newSourceMapping = new SourceMapping { RootEntityType = newEntityType };

                // TODO: should root be optional, in case of inheritance?
                var newNavigationTreeRoot = NavigationTreeNode.CreateRoot(newSourceMapping, fromMapping: new List<string>(), optional: false);
                newSourceMapping.NavigationTree = newNavigationTreeRoot;
                preProcessResult.state.SourceMappings = new List<SourceMapping> { newSourceMapping };

                var newPendingSelectorParameter = Expression.Parameter(newEntityType.ClrType, preProcessResult.state.CurrentParameter.Name);

                // since we just ran preprocessing and the method is OfType, pending selector is guaranteed to be simple e => e
                var newPendingSelectorBody = new NavigationPropertyBindingVisitor(newPendingSelectorParameter, preProcessResult.state.SourceMappings).Visit(newPendingSelectorParameter);

                preProcessResult.state.CurrentParameter = newPendingSelectorParameter;
                preProcessResult.state.PendingSelector = Expression.Lambda(newPendingSelectorBody, newPendingSelectorParameter);
            }

            var rewritten = methodCallExpression.Update(methodCallExpression.Object, new[] { preProcessResult.source });

            return new NavigationExpansionExpression(rewritten, preProcessResult.state, methodCallExpression.Type);
        }

        private Expression ProcessSkipTake(MethodCallExpression methodCallExpression)
        {
            // TODO: can probably re-use ProcessBasicTerminatingOperation (just make sure to visit arguments)
            // this can be done once we properly handle Include
            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            var preProcessResult = PreProcessTerminatingOperation(source);

            var newArgument = Visit(methodCallExpression.Arguments[1]);
            var rewritten = methodCallExpression.Update(methodCallExpression.Object, new[] { preProcessResult.source, newArgument });

            return new NavigationExpansionExpression(rewritten, preProcessResult.state, methodCallExpression.Type);
        }

        private Expression ProcessBasicTerminatingOperation(MethodCallExpression methodCallExpression)
        {
            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            var preProcessResult = PreProcessTerminatingOperation(source);
            var newArguments = methodCallExpression.Arguments.Skip(1).ToList();
            newArguments.Insert(0, preProcessResult.source);
            var rewritten = methodCallExpression.Update(methodCallExpression.Object, newArguments);

            return new NavigationExpansionExpression(rewritten, preProcessResult.state, methodCallExpression.Type);
        }

        private (Expression source, NavigationExpansionExpressionState state) PreProcessTerminatingOperation(NavigationExpansionExpression source)
        {
            var applyOrderingsResult = ApplyPendingOrderings(source.Operand, source.State);

            if (applyOrderingsResult.state.ApplyPendingSelector)
            {
                var unbinder = new NavigationPropertyUnbindingVisitor(applyOrderingsResult.state.CurrentParameter);
                var newSelectorBody = unbinder.Visit(applyOrderingsResult.state.PendingSelector.Body);

                var pssmg = new PendingSelectorSourceMappingGenerator(applyOrderingsResult.state.PendingSelector.Parameters[0], /*entityTypeOverride*/null);
                pssmg.Visit(applyOrderingsResult.state.PendingSelector);

                var selectorMethodInfo = IsQueryable(applyOrderingsResult.source.Type)
                    ? LinqMethodHelpers.QueryableSelectMethodInfo
                    : LinqMethodHelpers.EnumerableSelectMethodInfo;

                selectorMethodInfo = selectorMethodInfo.MakeGenericMethod(
                    applyOrderingsResult.state.CurrentParameter.Type,
                    newSelectorBody.Type);

                var result = Expression.Call(
                    selectorMethodInfo,
                    applyOrderingsResult.source,
                    Expression.Lambda(newSelectorBody, applyOrderingsResult.state.CurrentParameter));

                var newPendingSelectorParameter = Expression.Parameter(newSelectorBody.Type);
                var customRootMapping = new List<string>();

                // if the top level was navigation binding, then we are guaranteed to have exactly one source mapping in for the new pending selector
                var newPendingSelectorBody = applyOrderingsResult.state.PendingSelector.Body is NavigationBindingExpression binding
                    ? (Expression)new NavigationBindingExpression(
                        newPendingSelectorParameter,
                        pssmg.BindingToSourceMapping[binding].NavigationTree,
                        pssmg.BindingToSourceMapping[binding].RootEntityType,
                        pssmg.BindingToSourceMapping[binding],
                        newPendingSelectorParameter.Type)
                    : new CustomRootExpression(newPendingSelectorParameter, customRootMapping, newPendingSelectorParameter.Type);

                // TODO: only apply custom root mapping for parameters that are not root!
                var newState = new NavigationExpansionExpressionState(
                    newPendingSelectorParameter,
                    pssmg.SourceMappings,
                    Expression.Lambda(newPendingSelectorBody, newPendingSelectorParameter),
                    applyPendingSelector: false,
                    new List<(MethodInfo method, LambdaExpression keySelector)>(),
                    pendingIncludeChain: null,
                    pendingCardinalityReducingOperator: null,
                    new List<List<string>> { customRootMapping },
                    materializeCollectionNavigation: null
                    /*new List<NestedExpansionMapping>()*/);

                return (source: result, state: newState);
            }
            else
            {
                return (applyOrderingsResult.source, applyOrderingsResult.state);
            }
        }

        private Expression ProcessInclude(MethodCallExpression methodCallExpression)
        {
            // TODO: for now
            if (methodCallExpression.Arguments[1].Type == typeof(string))
            {
                return methodCallExpression;
            }

            var source = VisitSourceExpression(methodCallExpression.Arguments[0]);
            var includeLambda = methodCallExpression.Arguments[1].UnwrapQuote();
            AdjustCurrentParameterName(source.State, includeLambda.Parameters[0].Name);

            var applyOrderingsResult = ApplyPendingOrderings(source.Operand, source.State);

            // just bind to mark all the necessary navigation for include in the future
            // include need to be delayed, in case they are not needed, e.g. when there is a projection on top that only projects scalars
            Expression remappedIncludeLambdaBody;
            if (methodCallExpression.Method.Name == "Include")
            {
                remappedIncludeLambdaBody = ExpressionExtensions.CombineAndRemapLambdas(applyOrderingsResult.state.PendingSelector, includeLambda).Body;
            }
            else
            {
                // TODO: HACK - we can't use NavigationBindingVisitor for cases like root.Include(r => r.Collection).ThenInclude(r => r.Navigation)
                // because the type mismatch (trying to compose Navigation access on the ICollection from the first include
                // we manually construct navigation binding that should be a root of the new include, its EntityType being the element of the previously included collection
                // pendingIncludeLambda is only used for marking the includes - as long as the NavigationTreeNodes are correct it should be fine
                if (applyOrderingsResult.state.PendingIncludeChain.NavigationTreeNode.Navigation.IsCollection())
                {
                    var newIncludeLambdaRoot = new NavigationBindingExpression(
                        applyOrderingsResult.state.CurrentParameter,
                        applyOrderingsResult.state.PendingIncludeChain.NavigationTreeNode,
                        applyOrderingsResult.state.PendingIncludeChain.EntityType,
                        applyOrderingsResult.state.PendingIncludeChain.SourceMapping,
                        includeLambda.Parameters[0].Type);

                    remappedIncludeLambdaBody = new ExpressionReplacingVisitor(includeLambda.Parameters[0], newIncludeLambdaRoot).Visit(includeLambda.Body);
                }
                else
                {
                    var pendingIncludeChainLambda = Expression.Lambda(applyOrderingsResult.state.PendingIncludeChain, applyOrderingsResult.state.CurrentParameter);
                    remappedIncludeLambdaBody = ExpressionExtensions.CombineAndRemapLambdas(pendingIncludeChainLambda, includeLambda).Body;
                }
            }

            var binder = new NavigationPropertyBindingVisitor(applyOrderingsResult.state.PendingSelector.Parameters[0], applyOrderingsResult.state.SourceMappings, bindInclude: true);
            var boundIncludeLambdaBody = binder.Visit(remappedIncludeLambdaBody);

            if (boundIncludeLambdaBody is NavigationBindingExpression navigationBindingExpression)
            {
                applyOrderingsResult.state.PendingIncludeChain = navigationBindingExpression;
            }
            else
            {
                throw new InvalidOperationException("Incorrect include argument: " + includeLambda);
            }

            return new NavigationExpansionExpression(applyOrderingsResult.source, applyOrderingsResult.state, methodCallExpression.Type);
        }

        //private Expression ProcessTerminatingOperation(MethodCallExpression methodCallExpression)
        //{
        //    if (/*methodCallExpression.Method.MethodIsClosedFormOf(QueryableAnyMethodInfo)
        //        || */methodCallExpression.Method.MethodIsClosedFormOf(QueryableCountMethodInfo)
        //        //|| methodCallExpression.Method.MethodIsClosedFormOf(EnumerableAnyMethodInfo)
        //        || methodCallExpression.Method.MethodIsClosedFormOf(EnumerableCountMethodInfo))
        //    {
        //        return base.VisitMethodCall(methodCallExpression);
        //    }

        //    if (methodCallExpression.Method.MethodIsClosedFormOf(QueryableCountPredicateMethodInfo)
        //        || methodCallExpression.Method.MethodIsClosedFormOf(QueryableAnyPredicateMethodInfo))
        //    {
        //        return Visit(SimplifyPredicateMethod(methodCallExpression, queryable: true));
        //    }

        //    if (methodCallExpression.Method.MethodIsClosedFormOf(EnumerableCountPredicateMethodInfo)
        //        || methodCallExpression.Method.MethodIsClosedFormOf(EnumerableAnyPredicateMethodInfo))
        //    {
        //        return Visit(SimplifyPredicateMethod(methodCallExpression, queryable: false));
        //    }

        //    var sourceNee = (NavigationExpansionExpression)VisitSourceExpression(methodCallExpression.Arguments[0]);
        //    //var state = new NavigationExpansionExpressionState(Expression.Parameter(source.Type.GetSequenceType()));
        //    //if (source is NavigationExpansionExpression navigationExpansionExpression)
        //    {
        //        var source = sourceNee.Operand;
        //        var state = sourceNee.State;
        //        //var currentParameter = state.CurrentParameter;
        //        //state = sourceNee.State;
        //        //state.CurrentParameter = state.CurrentParameter ?? currentParameter;

        //        // TODO: test cases with casts, e.g. orders.Select(o => new { foo = (VipCustomer)o.Customer }).Distinct()
        //        var entityTypeOverride = methodCallExpression.Method.MethodIsClosedFormOf(QueryableOfType)
        //            ? _model.FindEntityType(methodCallExpression.Method.GetGenericArguments()[0])
        //            : null;

        //        if (state.ApplyPendingSelector)
        //        {
        //            var unbinder = new NavigationPropertyUnbindingVisitor(state.CurrentParameter);
        //            var newSelectorBody = unbinder.Visit(state.PendingSelector.Body);

        //            var pssmg = new PendingSelectorSourceMappingGenerator(state.PendingSelector.Parameters[0], entityTypeOverride);
        //            pssmg.Visit(state.PendingSelector);

        //            var selectorMethodInfo = IsQueryable(sourceNee.Operand.Type)
        //                ? QueryableSelectMethodInfo
        //                : EnumerableSelectMethodInfo;

        //            selectorMethodInfo = selectorMethodInfo.MakeGenericMethod(
        //                state.CurrentParameter.Type,
        //                newSelectorBody.Type);

        //            var result = Expression.Call(
        //                selectorMethodInfo,
        //                sourceNee.Operand,
        //                Expression.Lambda(newSelectorBody, state.CurrentParameter));

        //            var newPendingSelectorParameter = Expression.Parameter(entityTypeOverride?.ClrType ?? newSelectorBody.Type);
        //            var customRootMapping = new List<string>();

        //            // if the top level was navigation binding, then we are guaranteed to have exactly one source mapping in for the new pending selector
        //            var newPendingSelectorBody = state.PendingSelector.Body is NavigationBindingExpression binding
        //                ? (Expression)new NavigationBindingExpression(
        //                    newPendingSelectorParameter,
        //                    pssmg.BindingToSourceMapping[binding].NavigationTree,
        //                    pssmg.BindingToSourceMapping[binding].RootEntityType,
        //                    pssmg.BindingToSourceMapping[binding],
        //                    newPendingSelectorParameter.Type)
        //                : new CustomRootExpression(newPendingSelectorParameter, customRootMapping, newPendingSelectorParameter.Type);

        //            // TODO: only apply custom root mapping for parameters that are not root!
        //            state = new NavigationExpansionExpressionState(
        //                newPendingSelectorParameter,
        //                pssmg.SourceMappings,
        //                Expression.Lambda(newPendingSelectorBody, newPendingSelectorParameter),
        //                applyPendingSelector: false,
        //                new List<(MethodInfo method, LambdaExpression keySelector)>(),
        //                pendingCardinalityReducingOperator: null,
        //                new List<List<string>> { customRootMapping },
        //                materializeCollectionNavigation: null
        //                /*new List<NestedExpansionMapping>()*/);

        //            if (/*methodCallExpression.Method.MethodIsClosedFormOf(QueryableDistinctMethodInfo)
        //                || methodCallExpression.Method.MethodIsClosedFormOf(QueryableFirstMethodInfo)
        //                || methodCallExpression.Method.MethodIsClosedFormOf(QueryableFirstOrDefaultMethodInfo)
        //                || methodCallExpression.Method.MethodIsClosedFormOf(QueryableSingleMethodInfo)
        //                || methodCallExpression.Method.MethodIsClosedFormOf(QueryableSingleOrDefaultMethodInfo)
        //                || methodCallExpression.Method.MethodIsClosedFormOf(QueryableAny)
        //                || methodCallExpression.Method.MethodIsClosedFormOf(QueryableOfType)*/

        //                // TODO: fix this, use method infos instead, if this is the right place to handle Enumerables
        //                methodCallExpression.Method.Name == "Distinct"
        //                || methodCallExpression.Method.Name == "First"
        //                || methodCallExpression.Method.Name == "FirstOrDefault"
        //                || methodCallExpression.Method.Name == "Single"
        //                || methodCallExpression.Method.Name == "SingleOrDefault"
        //                || methodCallExpression.Method.Name == "Any"

        //                || methodCallExpression.Method.Name == "AsTracking"
        //                || methodCallExpression.Method.Name == "AsNoTracking")
        //            {
        //                var newMethod = methodCallExpression.Method.GetGenericMethodDefinition().MakeGenericMethod(
        //                    result.Type.GetGenericArguments()[0]);

        //                source = Expression.Call(newMethod, new[] { result });
        //            }
        //            else if (methodCallExpression.Method.Name == "OfType")
        //            {
        //                var newMethod = methodCallExpression.Method.GetGenericMethodDefinition().MakeGenericMethod(
        //                    entityTypeOverride.ClrType);

        //                source = Expression.Call(newMethod, new[] { result });
        //            }
        //            else if (/*methodCallExpression.Method.MethodIsClosedFormOf(QueryableTakeMethodInfo)
        //                || methodCallExpression.Method.MethodIsClosedFormOf(QueryableSkipMethodInfo)*/
        //                methodCallExpression.Method.Name == "Take"
        //                || methodCallExpression.Method.Name == "Skip"
        //                || methodCallExpression.Method.Name == "Include"
        //                || methodCallExpression.Method.Name == "ThenInclude"
        //                /*|| methodCallExpression.Method.MethodIsClosedFormOf(QueryableContains)*/)
        //            {
        //                var newArgument = Visit(methodCallExpression.Arguments[1]);
        //                source = methodCallExpression.Update(methodCallExpression.Object, new[] { result, newArgument });
        //            }
        //            else
        //            {
        //                throw new InvalidOperationException("Unsupported method " + methodCallExpression.Method.Name);
        //            }
        //        }
        //        else
        //        {
        //            var newArguments = methodCallExpression.Arguments.ToList();
        //            newArguments[0] = source;
        //            source = methodCallExpression.Update(methodCallExpression.Object, newArguments);

        //            if (entityTypeOverride != null)
        //            {
        //                var newSourceMapping = new SourceMapping
        //                {
        //                    RootEntityType = entityTypeOverride,
        //                };

        //                // TODO: should root be optional, in case of inheritance? (probably not)
        //                var newNavigationTreeRoot = NavigationTreeNode.CreateRoot(newSourceMapping, fromMapping: new List<string>(), optional: false);
        //                newSourceMapping.NavigationTree = newNavigationTreeRoot;
        //                state.SourceMappings = new List<SourceMapping> { newSourceMapping };
        //            }

        //            var newParameter = Expression.Parameter(entityTypeOverride?.ClrType ?? state.CurrentParameter.Type);
        //            state.CurrentParameter = newParameter;

        //            // mapping was replaced so we need to re-create pending selector bindings
        //            // since apply pending selector was false, pending selector is guaranteed to be simple e => e
        //            state.PendingSelector = (LambdaExpression)new NavigationPropertyBindingVisitor(newParameter, state.SourceMappings).Visit(Expression.Lambda(newParameter, newParameter));

        //            //var newPendingSelectorBody = new ExpressionReplacingVisitor(state.CurrentParameter, newParameter).Visit(state.PendingSelector.Body);
        //            //state.PendingSelector = Expression.Lambda(newPendingSelectorBody, newParameter);
        //            //state.CurrentParameter = newParameter;

        //            //if (entityTypeOverride != null)
        //            //{
        //            //    var newSourceMapping = new SourceMapping
        //            //    {
        //            //        RootEntityType = entityTypeOverride,
        //            //    };

        //            //    // TODO: should root be optional, in case of inheritance? (probably not)
        //            //    var newNavigationTreeRoot = NavigationTreeNode.CreateRoot(newSourceMapping, fromMapping: new List<string>(), optional: false);
        //            //    newSourceMapping.NavigationTree = newNavigationTreeRoot;
        //            //    state.SourceMappings = new List<SourceMapping> { newSourceMapping };

        //            //    // since apply pending selector was false, pending selector is guaranteed to be simple e => e
        //            //    state.PendingSelector = Expression.Lambda(
        //            //        new NavigationBindingExpression(newParameter, newNavigationTreeRoot, entityTypeOverride, newSourceMapping, entityTypeOverride.ClrType),
        //            //        newParameter);
        //            //}
        //        }

        //        // TODO: should we be reusing state?
        //        return new NavigationExpansionExpression(
        //            source,
        //            state,
        //            methodCallExpression.Type);
        //    }
        //}

        private MethodCallExpression SimplifyPredicateMethod(MethodCallExpression methodCallExpression, bool queryable)
        {
            var whereMethodInfo = queryable
                ? LinqMethodHelpers.QueryableWhereMethodInfo
                : LinqMethodHelpers.EnumerableWhereMethodInfo;

            var typeArgument = methodCallExpression.Arguments[0].Type.GetSequenceType();
            whereMethodInfo = whereMethodInfo.MakeGenericMethod(typeArgument);
            var whereMethodCall = Expression.Call(whereMethodInfo, methodCallExpression.Arguments[0], methodCallExpression.Arguments[1]);

            var newMethodInfo = GetNewMethodInfo(methodCallExpression.Method.Name, queryable);
            newMethodInfo = newMethodInfo.MakeGenericMethod(typeArgument);

            return Expression.Call(newMethodInfo, whereMethodCall);
        }

        private MethodInfo GetNewMethodInfo(string name, bool queryable)
        {
            if (queryable)
            {
                switch (name)
                {
                    case nameof(Queryable.Count):
                        return LinqMethodHelpers.QueryableCountMethodInfo;

                    case nameof(Queryable.First):
                        return LinqMethodHelpers.QueryableFirstMethodInfo;

                    case nameof(Queryable.FirstOrDefault):
                        return LinqMethodHelpers.QueryableFirstOrDefaultMethodInfo;

                    case nameof(Queryable.Single):
                        return LinqMethodHelpers.QueryableSingleMethodInfo;

                    case nameof(Queryable.SingleOrDefault):
                        return LinqMethodHelpers.QueryableSingleOrDefaultMethodInfo;

                    case nameof(Queryable.Any):
                        return LinqMethodHelpers.QueryableAnyMethodInfo;
                }
            }
            else
            {
                switch (name)
                {
                    case nameof(Enumerable.Count):
                        return LinqMethodHelpers.EnumerableCountMethodInfo;

                    case nameof(Enumerable.First):
                        return LinqMethodHelpers.EnumerableFirstMethodInfo;

                    case nameof(Enumerable.FirstOrDefault):
                        return LinqMethodHelpers.EnumerableFirstOrDefaultMethodInfo;

                    case nameof(Enumerable.Single):
                        return LinqMethodHelpers.EnumerableSingleMethodInfo;

                    case nameof(Enumerable.SingleOrDefault):
                        return LinqMethodHelpers.EnumerableSingleOrDefaultMethodInfo;

                    case nameof(Enumerable.Any):
                        return LinqMethodHelpers.EnumerableAnyMethodInfo;
                }
            }

            throw new InvalidOperationException("Invalid method name: " + name);
        }

        private Expression ProcessCardinalityReducingOperation(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.QueryableFirstPredicateMethodInfo)
                || methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.QueryableFirstOrDefaultPredicateMethodInfo)
                || methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.QueryableSinglePredicateMethodInfo)
                || methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.QueryableSingleOrDefaultPredicateMethodInfo))
            {
                return Visit(SimplifyPredicateMethod(methodCallExpression, queryable: true));
            }

            if (methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.EnumerableFirstPredicateMethodInfo)
                || methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.EnumerableFirstOrDefaultPredicateMethodInfo)
                || methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.EnumerableSinglePredicateMethodInfo)
                || methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.EnumerableSingleOrDefaultPredicateMethodInfo))
            {
                return Visit(SimplifyPredicateMethod(methodCallExpression, queryable: false));
            }

            var source = (NavigationExpansionExpression)VisitSourceExpression(methodCallExpression.Arguments[0]);
            var applyOrderingsResult = ApplyPendingOrderings(source.Operand, source.State);
            applyOrderingsResult.state.PendingCardinalityReducingOperator = methodCallExpression.Method.GetGenericMethodDefinition();

            return new NavigationExpansionExpression(applyOrderingsResult.source, applyOrderingsResult.state, methodCallExpression.Type);
        }

        protected override Expression VisitConstant(ConstantExpression constantExpression)
        {
            if (constantExpression.Value != null
                && constantExpression.Value.GetType().IsGenericType
                && constantExpression.Value.GetType().GetGenericTypeDefinition() == typeof(EntityQueryable<>))
            {
                var elementType = constantExpression.Value.GetType().GetSequenceType();
                var entityType = _model.FindEntityType(elementType);

                var sourceMapping = new SourceMapping
                {
                    RootEntityType = entityType,
                };

                var navigationTreeRoot = NavigationTreeNode.CreateRoot(sourceMapping, fromMapping: new List<string>(), optional: false);
                sourceMapping.NavigationTree = navigationTreeRoot;

                var pendingSelectorParameter = Expression.Parameter(entityType.ClrType);
                var pendingSelector = Expression.Lambda(
                    new NavigationBindingExpression(
                        pendingSelectorParameter,
                        navigationTreeRoot,
                        entityType,
                        sourceMapping,
                        pendingSelectorParameter.Type),
                    pendingSelectorParameter);

                var result = new NavigationExpansionExpression(
                    constantExpression,
                    new NavigationExpansionExpressionState(
                        pendingSelectorParameter,
                        new List<SourceMapping> { sourceMapping },
                        pendingSelector,
                        applyPendingSelector: false,
                        new List<(MethodInfo method, LambdaExpression keySelector)>(),
                        pendingIncludeChain: null,
                        pendingCardinalityReducingOperator: null,
                        new List<List<string>>(),
                        materializeCollectionNavigation: null
                        /*new List<NestedExpansionMapping>()*/),
                    constantExpression.Type);

                return result;
            }

            return base.VisitConstant(constantExpression);
        }

        private (Expression source, NavigationExpansionExpressionState state) ApplyPendingOrderings(Expression source, NavigationExpansionExpressionState state)
        {
            foreach (var pendingOrdering in state.PendingOrderings)
            {
                var remappedKeySelectorBody = new ExpressionReplacingVisitor(pendingOrdering.keySelector.Parameters[0], state.CurrentParameter).Visit(pendingOrdering.keySelector.Body);
                var newSelectorBody = new NavigationPropertyUnbindingVisitor(state.CurrentParameter).Visit(remappedKeySelectorBody);
                var newSelector = Expression.Lambda(newSelectorBody, state.CurrentParameter);
                var orderingMethod = pendingOrdering.method.MakeGenericMethod(state.CurrentParameter.Type, newSelectorBody.Type);
                source = Expression.Call(orderingMethod, source, newSelector);
            }

            state.PendingOrderings.Clear();

            return (source, state);
        }

        private (Expression source, Expression lambdaBody, NavigationExpansionExpressionState state) FindAndApplyNavigations(
            Expression source,
            LambdaExpression lambda,
            NavigationExpansionExpressionState state)
        {
            if (state.PendingSelector == null)
            {
                return (source, lambda.Body, state);
            }

            var remappedLambdaBody = ExpressionExtensions.CombineAndRemapLambdas(state.PendingSelector, lambda).Body;

            var binder = new NavigationPropertyBindingVisitor(
                state.PendingSelector.Parameters[0],
                state.SourceMappings);

            var boundLambdaBody = binder.Visit(remappedLambdaBody);
            boundLambdaBody = new NavigationComparisonOptimizingVisitor().Visit(boundLambdaBody);
            boundLambdaBody = new CollectionNavigationRewritingVisitor(state.CurrentParameter).Visit(boundLambdaBody);
            boundLambdaBody = Visit(boundLambdaBody);

            var result = (source, parameter: state.CurrentParameter);
            var applyPendingSelector = state.ApplyPendingSelector;

            foreach (var sourceMapping in state.SourceMappings)
            {
                if (sourceMapping.NavigationTree.Flatten().Any(n => n.ExpansionMode == NavigationTreeNodeExpansionMode.Pending))
                {
                    foreach (var navigationTree in sourceMapping.NavigationTree.Children)
                    {
                        if (navigationTree.Navigation.IsCollection())
                        {
                            throw new InvalidOperationException("Collections should not be part of the navigation tree: " + navigationTree.Navigation);
                        }

                        result = AddNavigationJoin(
                            result.source,
                            result.parameter,
                            sourceMapping,
                            navigationTree,
                            state,
                            new List<INavigation>());
                    }

                    applyPendingSelector = true;
                }
            }

            var pendingSelector = state.PendingSelector;
            if (state.CurrentParameter != result.parameter)
            {
                var pendingSelectorBody = new ExpressionReplacingVisitor(state.CurrentParameter, result.parameter).Visit(state.PendingSelector.Body);
                pendingSelector = Expression.Lambda(pendingSelectorBody, result.parameter);
                boundLambdaBody = new ExpressionReplacingVisitor(state.CurrentParameter, result.parameter).Visit(boundLambdaBody);
            }

            var newState = new NavigationExpansionExpressionState(
                result.parameter,
                state.SourceMappings,
                pendingSelector,
                applyPendingSelector,
                state.PendingOrderings,
                state.PendingIncludeChain,
                state.PendingCardinalityReducingOperator, // TODO: should we keep it?
                state.CustomRootMappings,
                state.MaterializeCollectionNavigation
                /*state.NestedExpansionMappings*/);

            // TODO: improve this (maybe a helper method?)
            if (source.Type.GetGenericTypeDefinition() == typeof(IOrderedQueryable<>)
                && result.source.Type.GetGenericTypeDefinition() == typeof(IQueryable<>))
            {
                var toOrderedQueryableMethod = typeof(NavigationExpansionExpression).GetMethod(nameof(NavigationExpansionExpression.ToOrderedQueryable)).MakeGenericMethod(result.source.Type.GetSequenceType());
                var toOrderedCall = Expression.Call(toOrderedQueryableMethod, result.source);

                return (source: toOrderedCall, lambdaBody: boundLambdaBody, state: newState);
            }
            else if (source.Type.GetGenericTypeDefinition() == typeof(IOrderedEnumerable<>)
                && result.source.Type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var toOrderedEnumerableMethod = typeof(NavigationExpansionExpression).GetMethod(nameof(NavigationExpansionExpression.ToOrderedEnumerable)).MakeGenericMethod(result.source.Type.GetSequenceType());
                var toOrderedCall = Expression.Call(toOrderedEnumerableMethod, result.source);

                return (source: toOrderedCall, lambdaBody: boundLambdaBody, state: newState);
            }

            return (result.source, lambdaBody: boundLambdaBody, state: newState);
        }

        private (Expression source, ParameterExpression parameter) AddNavigationJoin(
            Expression sourceExpression,
            ParameterExpression parameterExpression,
            SourceMapping sourceMapping,
            NavigationTreeNode navigationTree,
            NavigationExpansionExpressionState state,
            List<INavigation> navigationPath)
        {
            if (navigationTree.ExpansionMode == NavigationTreeNodeExpansionMode.Pending)
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
                    foreach (var navigationTreeNode in mapping.NavigationTree.Flatten().Where(n => n.ExpansionMode == NavigationTreeNodeExpansionMode.Complete && n != navigationTree))
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

                navigationTree.ExpansionMode = NavigationTreeNodeExpansionMode.Complete;
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
                    navigationPath.ToList());
            }

            return result;
        }

        private void RemapNavigationChain(NavigationTreeNode navigationTreeNode, bool optional)
        {
            if (navigationTreeNode != null)
            {
                navigationTreeNode.ToMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
                if (optional)
                {
                    navigationTreeNode.ToMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
                }

                RemapNavigationChain(navigationTreeNode.Parent, optional);
            }
        }

        private (LambdaExpression lambda, NavigationExpansionExpressionState state) RemapTwoArgumentResultSelector(
            LambdaExpression resultSelector,
            NavigationExpansionExpressionState outerState,
            NavigationExpansionExpressionState innerState)
        {
            var resultSelectorBody = resultSelector.Body;
            var remappedResultSelector = ExpressionExtensions.CombineAndRemapLambdas(outerState.PendingSelector, resultSelector, resultSelector.Parameters[0]);
            remappedResultSelector = ExpressionExtensions.CombineAndRemapLambdas(innerState.PendingSelector, remappedResultSelector, remappedResultSelector.Parameters[1]);

            var outerBinder = new NavigationPropertyBindingVisitor(
                outerState.CurrentParameter,
                outerState.SourceMappings);

            var innerBinder = new NavigationPropertyBindingVisitor(
                innerState.CurrentParameter,
                innerState.SourceMappings);

            var boundResultSelectorBody = outerBinder.Visit(remappedResultSelector.Body);
            boundResultSelectorBody = innerBinder.Visit(boundResultSelectorBody);

            foreach (var outerCustomRootMapping in outerState.CustomRootMappings)
            {
                outerCustomRootMapping.Insert(0, nameof(TransparentIdentifier2<object, object>.Outer));
            }

            //foreach (var outerNestedExpansionMapping in outerState.NestedExpansionMappings)
            //{
            //    outerNestedExpansionMapping.Path.Insert(0, nameof(TransparentIdentifier2<object, object>.Outer));
            //}

            foreach (var outerSourceMapping in outerState.SourceMappings)
            {
                foreach (var navigationTreeNode in outerSourceMapping.NavigationTree.Flatten().Where(n => n.ExpansionMode == NavigationTreeNodeExpansionMode.Complete))
                {
                    navigationTreeNode.ToMapping.Insert(0, nameof(TransparentIdentifier2<object, object>.Outer));
                    foreach (var fromMapping in navigationTreeNode.FromMappings)
                    {
                        fromMapping.Insert(0, nameof(TransparentIdentifier2<object, object>.Outer));
                    }
                }
            }

            foreach (var innerCustomRootMapping in innerState.CustomRootMappings)
            {
                innerCustomRootMapping.Insert(0, nameof(TransparentIdentifier2<object, object>.Inner));
            }

            //foreach (var innerNestedExpansionMapping in innerState.NestedExpansionMappings)
            //{
            //    innerNestedExpansionMapping.Path.Insert(0, nameof(TransparentIdentifier2<object, object>.Inner));
            //}

            foreach (var innerSourceMapping in innerState.SourceMappings)
            {
                foreach (var navigationTreeNode in innerSourceMapping.NavigationTree.Flatten().Where(n => n.ExpansionMode == NavigationTreeNodeExpansionMode.Complete))
                {
                    navigationTreeNode.ToMapping.Insert(0, nameof(TransparentIdentifier2<object, object>.Inner));
                    foreach (var fromMapping in navigationTreeNode.FromMappings)
                    {
                        fromMapping.Insert(0, nameof(TransparentIdentifier2<object, object>.Inner));
                    }
                }
            }

            var resultType = typeof(TransparentIdentifier2<,>).MakeGenericType(outerState.CurrentParameter.Type, innerState.CurrentParameter.Type);
            var transparentIdentifierCtorInfo = resultType.GetTypeInfo().GetConstructors().Single();
            var transparentIdentifierParameter = Expression.Parameter(resultType, "join");

            var newPendingSelectorBody = new ExpressionReplacingVisitor(outerState.CurrentParameter, transparentIdentifierParameter).Visit(boundResultSelectorBody);
            newPendingSelectorBody = new ExpressionReplacingVisitor(innerState.CurrentParameter, transparentIdentifierParameter).Visit(newPendingSelectorBody);

            var newState = new NavigationExpansionExpressionState(
                transparentIdentifierParameter,
                outerState.SourceMappings.Concat(innerState.SourceMappings).ToList(),
                Expression.Lambda(newPendingSelectorBody, transparentIdentifierParameter),
                applyPendingSelector: true,
                new List<(MethodInfo method, LambdaExpression keySelector)>(),
                pendingIncludeChain: null,
                pendingCardinalityReducingOperator: null, // TODO: incorrect?
                outerState.CustomRootMappings.Concat(innerState.CustomRootMappings).ToList(),
                materializeCollectionNavigation: null
                /*outerState.NestedExpansionMappings.Concat(innerState.NestedExpansionMappings).ToList()*/);

            var lambda = Expression.Lambda(
                Expression.New(transparentIdentifierCtorInfo, outerState.CurrentParameter, innerState.CurrentParameter),
                outerState.CurrentParameter,
                innerState.CurrentParameter);

            return (lambda, state: newState);
        }

        private (LambdaExpression lambda, NavigationExpansionExpressionState state) RemapGroupJoinResultSelector(
            LambdaExpression resultSelector,
            NavigationExpansionExpressionState outerState)
        {

            return (null, null);
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

        private class PendingSelectorSourceMappingGenerator : ExpressionVisitor
        {
            private ParameterExpression _rootParameter;
            private List<string> _currentPath = new List<string>();
            private IEntityType _entityTypeOverride;

            public List<SourceMapping> SourceMappings = new List<SourceMapping>();

            public Dictionary<NavigationBindingExpression, SourceMapping> BindingToSourceMapping
                = new Dictionary<NavigationBindingExpression, SourceMapping>();

            public PendingSelectorSourceMappingGenerator(ParameterExpression rootParameter, IEntityType entityTypeOverride)
            {
                _rootParameter = rootParameter;
                _entityTypeOverride = entityTypeOverride;
            }

            // prune these nodes, we only want to look for entities accessible in the result
            protected override Expression VisitMember(MemberExpression memberExpression)
                => memberExpression;

            protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
                => methodCallExpression;

            // TODO: coalesce should pass? - TEST!
            protected override Expression VisitBinary(BinaryExpression binaryExpression)
                => binaryExpression;

            protected override Expression VisitUnary(UnaryExpression unaryExpression)
            {
                // TODO: handle cast here?

                return base.VisitUnary(unaryExpression);
            }

            protected override Expression VisitNew(NewExpression newExpression)
            {
                // TODO: when constructing a DTO, there will be arguments present, but no members - is it correct to just skip in this case?
                if (newExpression.Members != null)
                {
                    for (var i = 0; i < newExpression.Arguments.Count; i++)
                    {
                        _currentPath.Add(newExpression.Members[i].Name);
                        Visit(newExpression.Arguments[i]);
                        _currentPath.RemoveAt(_currentPath.Count - 1);
                    }
                }

                return newExpression;
            }

            protected override Expression VisitExtension(Expression extensionExpression)
            {
                if (extensionExpression is NavigationBindingExpression navigationBindingExpression)
                {
                    if (navigationBindingExpression.RootParameter == _rootParameter)
                    {
                        var sourceMapping = new SourceMapping
                        {
                            RootEntityType = _entityTypeOverride ?? navigationBindingExpression.EntityType,
                        };

                        var navigationTreeRoot = NavigationTreeNode.CreateRoot(sourceMapping, _currentPath.ToList(), navigationBindingExpression.NavigationTreeNode.Optional);
                        sourceMapping.NavigationTree = navigationTreeRoot;

                        SourceMappings.Add(sourceMapping);
                        BindingToSourceMapping[navigationBindingExpression] = sourceMapping;
                    }

                    return extensionExpression;
                }

                // TODO: is this correct or some processing is needed here?
                if (extensionExpression is CustomRootExpression customRootExpression)
                {
                    return customRootExpression;
                }

                return base.VisitExtension(extensionExpression);
            }
        }
    }
}

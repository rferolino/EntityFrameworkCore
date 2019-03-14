// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query.Expressions.Internal;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion.Visitors
{
    public class NavigationExpansionVisitorBase : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression extensionExpression)
        {
            if (extensionExpression is NavigationBindingExpression navigationBindingExpression)
            {
                var newRootParameter = (ParameterExpression)Visit(navigationBindingExpression.RootParameter);

                return newRootParameter != navigationBindingExpression.RootParameter
                    ? new NavigationBindingExpression(
                        newRootParameter,
                        navigationBindingExpression.NavigationTreeNode,
                        navigationBindingExpression.EntityType,
                        navigationBindingExpression.SourceMapping,
                        navigationBindingExpression.Type)
                    : navigationBindingExpression;
            }

            if (extensionExpression is CustomRootExpression customRootExpression)
            {
                var newRootParameter = (ParameterExpression)Visit(customRootExpression.RootParameter);

                return newRootParameter != customRootExpression.RootParameter
                    ? new CustomRootExpression(newRootParameter, customRootExpression.Mapping, customRootExpression.Type)
                    : customRootExpression;
            }

            if (extensionExpression is NavigationExpansionRootExpression navigationExpansionRootExpression)
            {
                var newNavigationExpansion = (NavigationExpansionExpression)Visit(navigationExpansionRootExpression.NavigationExpansion);

                return newNavigationExpansion != navigationExpansionRootExpression.NavigationExpansion
                    ? new NavigationExpansionRootExpression(newNavigationExpansion, navigationExpansionRootExpression.Mapping)
                    : navigationExpansionRootExpression;
            }

            if (extensionExpression is NavigationExpansionExpression navigationExpansionExpression)
            {
                var newOperand = Visit(navigationExpansionExpression.Operand);
                var newState = ReplaceNavigationExpansionExpressionState(navigationExpansionExpression.State);

                return newOperand != navigationExpansionExpression.Operand
                    || newState != navigationExpansionExpression.State
                    ? new NavigationExpansionExpression(
                        newOperand,
                        newState,
                        navigationExpansionExpression.Type)
                    : navigationExpansionExpression;
            }
            
            if (extensionExpression is CorrelationPredicateExpression correlationPredicateExpression)
            {
                var newOuterKeyNullCheck = Visit(correlationPredicateExpression.OuterKeyNullCheck);
                var newEqualExpression = (BinaryExpression)Visit(correlationPredicateExpression.EqualExpression);
                //var newNavigationRootExpression = Visit(nullSafeEqualExpression.NavigationRootExpression);

                return newOuterKeyNullCheck != correlationPredicateExpression.OuterKeyNullCheck || newEqualExpression != correlationPredicateExpression.EqualExpression// || newNavigationRootExpression != nullSafeEqualExpression.NavigationRootExpression
                    ? new CorrelationPredicateExpression(newOuterKeyNullCheck, newEqualExpression/*, nullSafeEqualExpression.NavigationRootExpression, nullSafeEqualExpression.Navigations*/)
                    : correlationPredicateExpression;
            }

            //if (extensionExpression is NullSafeEqualExpression nullSafeEqualExpression)
            //{
            //    var newOuterKeyNullCheck = Visit(nullSafeEqualExpression.OuterKeyNullCheck);
            //    var newEqualExpression = (BinaryExpression)Visit(nullSafeEqualExpression.EqualExpression);
            //    var newNavigationRootExpression = Visit(nullSafeEqualExpression.NavigationRootExpression);

            //    return newOuterKeyNullCheck != nullSafeEqualExpression.OuterKeyNullCheck || newEqualExpression != nullSafeEqualExpression.EqualExpression || newNavigationRootExpression != nullSafeEqualExpression.NavigationRootExpression
            //        ? new NullSafeEqualExpression(newOuterKeyNullCheck, newEqualExpression, nullSafeEqualExpression.NavigationRootExpression, nullSafeEqualExpression.Navigations)
            //        : nullSafeEqualExpression;
            //}

            if (extensionExpression is NullConditionalExpression nullConditionalExpression)
            {
                var newCaller = Visit(nullConditionalExpression.Caller);
                var newAccessOperation = Visit(nullConditionalExpression.AccessOperation);

                return newCaller != nullConditionalExpression.Caller || newAccessOperation != nullConditionalExpression.AccessOperation
                    ? new NullConditionalExpression(newCaller, newAccessOperation)
                    : nullConditionalExpression;
            }

            if (extensionExpression is IncludeExpression includeExpression)
            {
                var newEntityExpression = Visit(includeExpression.EntityExpression);
                var newNavigationExpression = Visit(includeExpression.NavigationExpression);

                return newEntityExpression != includeExpression.EntityExpression || newNavigationExpression != includeExpression.NavigationExpression
                    ? new IncludeExpression(newEntityExpression, newNavigationExpression, includeExpression.Navigation)
                    : includeExpression;
            }

            throw new System.InvalidOperationException("Unhandled operator: " + extensionExpression);
        }

        private NavigationExpansionExpressionState ReplaceNavigationExpansionExpressionState(NavigationExpansionExpressionState state)
        {
            var newCurrentParameter = (ParameterExpression)Visit(state.CurrentParameter);
            //var newPendingSelectorBody = Visit(state.PendingSelector.Body);
            var newPendingSelector = (LambdaExpression)Visit(state.PendingSelector);
            var pendingOrderingsChanged = false;
            var newPendingOrderings = new List<(MethodInfo method, LambdaExpression keySelector)>();

            foreach (var pendingOrdering in state.PendingOrderings)
            {
                var newPendingOrderingKeySelector = (LambdaExpression)Visit(pendingOrdering.keySelector);
                if (newPendingOrderingKeySelector != pendingOrdering.keySelector)
                {
                    newPendingOrderings.Add((pendingOrdering.method, keySelector: newPendingOrderingKeySelector));
                    pendingOrderingsChanged = true;
                }
                else
                {
                    newPendingOrderings.Add(pendingOrdering);
                }
            }

            var newPendingIncludeChain = (NavigationBindingExpression)Visit(state.PendingIncludeChain);

            if (newCurrentParameter != state.CurrentParameter
                || newPendingSelector != state.PendingSelector
                || pendingOrderingsChanged
                || newPendingIncludeChain != state.PendingIncludeChain)
            {
                return new NavigationExpansionExpressionState(
                    newCurrentParameter,
                    state.SourceMappings,
                    newPendingSelector,
                    state.ApplyPendingSelector,
                    newPendingOrderings,
                    newPendingIncludeChain,
                    state.PendingCardinalityReducingOperator,
                    state.PendingTags,
                    state.CustomRootMappings,
                    state.MaterializeCollectionNavigation);
            }

            return state;
        }

        //protected override Expression VisitLambda<T>(Expression<T> lambdaExpression)
        //{
        //    var newParameters = new List<ParameterExpression>();
        //    var parameterChanged = false;

        //    foreach (var parameter in lambdaExpression.Parameters)
        //    {
        //        var newParameter = (ParameterExpression)Visit(parameter);
        //        newParameters.Add(newParameter);
        //        if (newParameter != parameter)
        //        {
        //            parameterChanged = true;
        //        }
        //    }

        //    var newBody = Visit(lambdaExpression.Body);

        //    return parameterChanged || newBody != lambdaExpression.Body
        //        ? Expression.Lambda(newBody, newParameters)
        //        : lambdaExpression;
        //}
    }
}

// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
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
        private IModel _model;

        public NavigationExpandingVisitor(IModel model)
        {
            _model = model;
        }

        protected override Expression VisitExtension(Expression extensionExpression)
        {
            if (extensionExpression is NavigationBindingExpression navigationBindingExpression)
            {
                return navigationBindingExpression;
            }

            if (extensionExpression is CustomRootExpression customRootExpression)
            {
                return customRootExpression;
            }

            if (extensionExpression is NavigationExpansionRootExpression navigationExpansionRootExpression)
            {
                return navigationExpansionRootExpression;
            }

            if (extensionExpression is IncludeExpression includeExpression)
            {
                return includeExpression;
            }

            if (extensionExpression is NavigationExpansionExpression navigationExpansionExpression)
            {
                return navigationExpansionExpression;
            }

            return base.VisitExtension(extensionExpression);
        }

        private Expression ProcessMemberPushdown(
            Expression source,
            NavigationExpansionExpression navigationExpansionExpression,
            bool efProperty,
            MemberInfo memberInfo,
            string propertyName,
            Type resultType)
        {
            var selectorParameter = Expression.Parameter(source.Type, navigationExpansionExpression.State.CurrentParameter.Name);

            var selectorBody = efProperty
                ? (Expression)Expression.Call(EF.PropertyMethod.MakeGenericMethod(resultType),
                    selectorParameter,
                    Expression.Constant(propertyName))
                : Expression.MakeMemberAccess(selectorParameter, memberInfo);

            // TODO: do we need to check methods with predicate, or are they guaranteed to be optimized into Where().Method()?
            if (navigationExpansionExpression.State.PendingCardinalityReducingOperator.MethodIsClosedFormOf(LinqMethodHelpers.QueryableFirstOrDefaultMethodInfo)
                || navigationExpansionExpression.State.PendingCardinalityReducingOperator.MethodIsClosedFormOf(LinqMethodHelpers.QueryableFirstOrDefaultPredicateMethodInfo)
                || navigationExpansionExpression.State.PendingCardinalityReducingOperator.MethodIsClosedFormOf(LinqMethodHelpers.QueryableSingleOrDefaultMethodInfo)
                || navigationExpansionExpression.State.PendingCardinalityReducingOperator.MethodIsClosedFormOf(LinqMethodHelpers.QueryableSingleOrDefaultPredicateMethodInfo)
                || navigationExpansionExpression.State.PendingCardinalityReducingOperator.MethodIsClosedFormOf(LinqMethodHelpers.EnumerableFirstOrDefaultMethodInfo)
                || navigationExpansionExpression.State.PendingCardinalityReducingOperator.MethodIsClosedFormOf(LinqMethodHelpers.EnumerableFirstOrDefaultPredicateMethodInfo)
                || navigationExpansionExpression.State.PendingCardinalityReducingOperator.MethodIsClosedFormOf(LinqMethodHelpers.EnumerableSingleOrDefaultMethodInfo)
                || navigationExpansionExpression.State.PendingCardinalityReducingOperator.MethodIsClosedFormOf(LinqMethodHelpers.EnumerableSingleOrDefaultPredicateMethodInfo))
            {
                if (!selectorBody.Type.IsNullableType())
                {
                    selectorBody = Expression.Convert(selectorBody, selectorBody.Type.MakeNullable());
                }
            }

            var selector = Expression.Lambda(selectorBody, selectorParameter);
            var remappedSelectorBody = ExpressionExtensions.CombineAndRemap(selector.Body, selectorParameter, navigationExpansionExpression.State.PendingSelector.Body);

            var binder = new NavigationPropertyBindingVisitor(
                navigationExpansionExpression.State.CurrentParameter,
                navigationExpansionExpression.State.SourceMappings);

            var boundSelectorBody = binder.Visit(remappedSelectorBody);
            if (boundSelectorBody is NavigationBindingExpression navigationBindingExpression
                && navigationBindingExpression.NavigationTreeNode.Navigation is INavigation lastNavigation
                && lastNavigation != null)
            {
                if (lastNavigation.IsCollection())
                {
                    var collectionNavigationElementType = lastNavigation.ForeignKey.DeclaringEntityType.ClrType;
                    var entityQueryable = NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(collectionNavigationElementType);
                    var outerParameter = Expression.Parameter(collectionNavigationElementType, collectionNavigationElementType.GenerateParameterName());

                    var outerKeyAccess = NavigationExpansionHelpers.CreateKeyAccessExpression(
                        outerParameter,
                        lastNavigation.ForeignKey.Properties);

                    var innerParameter = Expression.Parameter(navigationExpansionExpression.Type);
                    var innerKeyAccessLambda = Expression.Lambda(
                        NavigationExpansionHelpers.CreateKeyAccessExpression(
                            innerParameter,
                            lastNavigation.ForeignKey.PrincipalKey.Properties),
                        innerParameter);

                    var combinedKeySelectorBody = ExpressionExtensions.CombineAndRemap(innerKeyAccessLambda.Body, innerKeyAccessLambda.Parameters[0], navigationExpansionExpression.State.PendingSelector.Body);

                    // TODO: properly compare combinedKeySelectorBody with outerKeyAccess for nullability match
                    if (outerKeyAccess.Type != combinedKeySelectorBody.Type)
                    {
                        if (combinedKeySelectorBody.Type.IsNullableType())
                        {
                            outerKeyAccess = Expression.Convert(outerKeyAccess, combinedKeySelectorBody.Type);
                        }
                        else
                        {
                            combinedKeySelectorBody = Expression.Convert(combinedKeySelectorBody, outerKeyAccess.Type);
                        }
                    }

                    var rewrittenState = new NavigationExpansionExpressionState(
                        navigationExpansionExpression.State.CurrentParameter,
                        navigationExpansionExpression.State.SourceMappings,
                        Expression.Lambda(combinedKeySelectorBody, navigationExpansionExpression.State.CurrentParameter),
                        applyPendingSelector: true,
                        navigationExpansionExpression.State.PendingOrderings,
                        navigationExpansionExpression.State.PendingIncludeChain,
                        navigationExpansionExpression.State.PendingCardinalityReducingOperator,
                        navigationExpansionExpression.State.PendingTags,
                        navigationExpansionExpression.State.CustomRootMappings,
                        materializeCollectionNavigation: null);

                    var rewrittenNavigationExpansionExpression = new NavigationExpansionExpression(navigationExpansionExpression.Operand, rewrittenState, combinedKeySelectorBody.Type);
                    var inner = new NavigationExpansionReducingVisitor().Visit(rewrittenNavigationExpansionExpression);

                    var predicate = Expression.Lambda(
                        Expression.Equal(outerKeyAccess, inner),
                        outerParameter);

                    var whereMethodInfo = LinqMethodHelpers.QueryableWhereMethodInfo.MakeGenericMethod(collectionNavigationElementType);
                    var rewritten = Expression.Call(
                        whereMethodInfo,
                        entityQueryable,
                        predicate);

                    var entityType = lastNavigation.ForeignKey.DeclaringEntityType;

                    // TODO: copied from visit constant - DRY !!!!
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

                    // TODO: should we compensate for nullability difference here also?
                    return new NavigationExpansionExpression(
                        rewritten,
                        new NavigationExpansionExpressionState(
                            pendingSelectorParameter,
                            new List<SourceMapping> { sourceMapping },
                            pendingSelector,
                            applyPendingSelector: false,
                            new List<(MethodInfo method, LambdaExpression keySelector)>(),
                            pendingIncludeChain: null,
                            pendingCardinalityReducingOperator: null, // TODO: incorrect?
                            pendingTags: new List<string>(),
                            customRootMappings: new List<List<string>>(),
                            materializeCollectionNavigation: null),
                        rewritten.Type);
                }
                else
                {
                    return ProcessSelectCore(
                        navigationExpansionExpression.Operand,
                        navigationExpansionExpression.State,
                        selector,
                        selectorBody.Type);
                }
            }

            // TODO idk if thats needed
            var newState = new NavigationExpansionExpressionState(
                navigationExpansionExpression.State.CurrentParameter,
                navigationExpansionExpression.State.SourceMappings,
                Expression.Lambda(boundSelectorBody, navigationExpansionExpression.State.CurrentParameter),
                applyPendingSelector: true,
                navigationExpansionExpression.State.PendingOrderings,
                navigationExpansionExpression.State.PendingIncludeChain,
                navigationExpansionExpression.State.PendingCardinalityReducingOperator,
                navigationExpansionExpression.State.PendingTags,
                navigationExpansionExpression.State.CustomRootMappings,
                navigationExpansionExpression.State.MaterializeCollectionNavigation);

            // TODO: expand navigations

            var result = new NavigationExpansionExpression(
                navigationExpansionExpression.Operand,
                newState,
                selectorBody.Type);

            return resultType != result.Type
                ? (Expression)Expression.Convert(result, resultType)
                : result;
        }

        protected override Expression VisitMember(MemberExpression memberExpression)
        {
            var newExpression = Visit(memberExpression.Expression);
            if (newExpression is NavigationExpansionExpression navigationExpansionExpression
                && navigationExpansionExpression.State.PendingCardinalityReducingOperator != null)
            {
                return ProcessMemberPushdown(newExpression, navigationExpansionExpression, efProperty: false, memberExpression.Member, propertyName: null, memberExpression.Type);
            }

            return base.VisitMember(memberExpression);
        }

        //protected override Expression VisitUnary(UnaryExpression unaryExpression)
        //{
        //    var newOperand = Visit(unaryExpression.Operand);

        //    if (unaryExpression.NodeType == ExpressionType.Convert
        //        || unaryExpression.NodeType == ExpressionType.ConvertChecked)
        //    {
        //        if (newOperand.Type != unaryExpression.Operand.Type)
        //        {
        //            return unaryExpression.NodeType == ExpressionType.Convert
        //                ? Expression.Convert(newOperand, unaryExpression.Type)
        //                : Expression.ConvertChecked(newOperand, unaryExpression.Type);
        //        }
        //    }

        //    return unaryExpression.Update(newOperand);
        //}

        protected override Expression VisitBinary(BinaryExpression binaryExpression)
        {
            var leftConstantNull = binaryExpression.Left.IsNullConstantExpression();
            var rightConstantNull = binaryExpression.Right.IsNullConstantExpression();

            // collection comparison must be optimized out before we visit the left and right
            // otherwise collections would be rewriteen and harder to identify
            if (binaryExpression.NodeType == ExpressionType.Equal
                || binaryExpression.NodeType == ExpressionType.NotEqual)
            {
                var leftParent = default(Expression);
                var leftNavigation = default(INavigation);
                var rightParent = default(Expression);
                var rightNavigation = default(INavigation);

                // TODO: this is hacky and won't work for weak entity types
                // also, add support for EF.Property and maybe convert node around the navigation
                if (binaryExpression.Left is MemberExpression leftMember
                    && leftMember.Type.TryGetSequenceType() is Type leftSequenceType
                    && leftSequenceType != null
                    && _model.FindEntityType(leftMember.Expression.Type) is IEntityType leftParentEntityType)
                {
                    leftNavigation = leftParentEntityType.FindNavigation(leftMember.Member.Name);
                    if (leftNavigation != null)
                    {
                        leftParent = leftMember.Expression;
                    }
                }

                if (binaryExpression.Right is MemberExpression rightMember
                    && rightMember.Type.TryGetSequenceType() is Type rightSequenceType
                    && rightSequenceType != null
                    && _model.FindEntityType(rightMember.Expression.Type) is IEntityType rightParentEntityType)
                {
                    rightNavigation = rightParentEntityType.FindNavigation(rightMember.Member.Name);
                    if (rightNavigation != null)
                    {
                        rightParent = rightMember.Expression;
                    }
                }

                if (leftNavigation != null
                    && leftNavigation.IsCollection()
                    && leftNavigation == rightNavigation)
                {
                    var rewritten = Expression.MakeBinary(binaryExpression.NodeType, leftParent, rightParent);

                    return Visit(rewritten);
                }

                if (leftNavigation != null
                    && leftNavigation.IsCollection()
                    && rightConstantNull)
                {
                    var rewritten = Expression.MakeBinary(binaryExpression.NodeType, leftParent, Expression.Constant(null));

                    return Visit(rewritten);
                }

                if (rightNavigation != null
                    && rightNavigation.IsCollection()
                    && leftConstantNull)
                {
                    var rewritten = Expression.MakeBinary(binaryExpression.NodeType, Expression.Constant(null), rightParent);

                    return Visit(rewritten);
                }
            }

            var newLeft = Visit(binaryExpression.Left);
            var newRight = Visit(binaryExpression.Right);

            if (binaryExpression.NodeType == ExpressionType.Equal
                || binaryExpression.NodeType == ExpressionType.NotEqual)
            {
                var leftNavigationExpansionExpression = newLeft as NavigationExpansionExpression;
                var rightNavigationExpansionExpression = newRight as NavigationExpansionExpression;
                var leftNavigationBindingExpression = default(NavigationBindingExpression);
                var rightNavigationBindingExpression = default(NavigationBindingExpression);

                if (leftNavigationExpansionExpression?.State.PendingCardinalityReducingOperator != null)
                {
                    leftNavigationBindingExpression = leftNavigationExpansionExpression.State.PendingSelector.Body as NavigationBindingExpression;
                }

                if (rightNavigationExpansionExpression?.State.PendingCardinalityReducingOperator != null)
                {
                    rightNavigationBindingExpression = rightNavigationExpansionExpression.State.PendingSelector.Body as NavigationBindingExpression;
                }

                // TODO: same is done on the other side - DRY
                if (leftNavigationBindingExpression != null
                    && rightConstantNull)
                {
                    var outerKeyAccess = NavigationExpansionHelpers.CreateKeyAccessExpression(
                        leftNavigationBindingExpression,
                        leftNavigationBindingExpression.EntityType.FindPrimaryKey().Properties,
                        addNullCheck: true);

                    var innerKeyAccess = NavigationExpansionHelpers.CreateNullKeyExpression(
                        outerKeyAccess.Type,
                        leftNavigationBindingExpression.EntityType.FindPrimaryKey().Properties.Count);

                    var newLeftNavigationExpansionExpressionState = new NavigationExpansionExpressionState(
                        leftNavigationExpansionExpression.State.CurrentParameter,
                        leftNavigationExpansionExpression.State.SourceMappings,
                        Expression.Lambda(outerKeyAccess, leftNavigationExpansionExpression.State.PendingSelector.Parameters[0]),
                        applyPendingSelector: true,
                        leftNavigationExpansionExpression.State.PendingOrderings,
                        leftNavigationExpansionExpression.State.PendingIncludeChain,
                        leftNavigationExpansionExpression.State.PendingCardinalityReducingOperator,
                        leftNavigationExpansionExpression.State.PendingTags,
                        leftNavigationExpansionExpression.State.CustomRootMappings,
                        leftNavigationExpansionExpression.State.MaterializeCollectionNavigation);

                    newLeft = new NavigationExpansionExpression(
                        leftNavigationExpansionExpression.Operand,
                        newLeftNavigationExpansionExpressionState,
                        outerKeyAccess.Type);

                    newRight = innerKeyAccess;
                }

                if (rightNavigationBindingExpression != null
                    && leftConstantNull)
                {
                    var innerKeyAccess = NavigationExpansionHelpers.CreateKeyAccessExpression(
                        rightNavigationBindingExpression,
                        rightNavigationBindingExpression.EntityType.FindPrimaryKey().Properties,
                        addNullCheck: true);

                    var outerKeyAccess = NavigationExpansionHelpers.CreateNullKeyExpression(
                        innerKeyAccess.Type,
                        rightNavigationBindingExpression.EntityType.FindPrimaryKey().Properties.Count);

                    var newRightNavigationExpansionExpressionState = new NavigationExpansionExpressionState(
                        rightNavigationExpansionExpression.State.CurrentParameter,
                        rightNavigationExpansionExpression.State.SourceMappings,
                        Expression.Lambda(innerKeyAccess, rightNavigationExpansionExpression.State.PendingSelector.Parameters[0]),
                        applyPendingSelector: true,
                        rightNavigationExpansionExpression.State.PendingOrderings,
                        rightNavigationExpansionExpression.State.PendingIncludeChain,
                        rightNavigationExpansionExpression.State.PendingCardinalityReducingOperator,
                        rightNavigationExpansionExpression.State.PendingTags,
                        rightNavigationExpansionExpression.State.CustomRootMappings,
                        rightNavigationExpansionExpression.State.MaterializeCollectionNavigation);

                    newRight = new NavigationExpansionExpression(
                        rightNavigationExpansionExpression.Operand,
                        newRightNavigationExpansionExpressionState,
                        innerKeyAccess.Type);

                    newLeft = outerKeyAccess;
                }

                var result = binaryExpression.NodeType == ExpressionType.Equal
                    ? Expression.Equal(newLeft, newRight)
                    : Expression.NotEqual(newLeft, newRight);

                return result;
            }

            return binaryExpression.Update(newLeft, binaryExpression.Conversion, newRight);
        }
    }
}

// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Extensions.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.Expressions.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion.Visitors
{
    /// <summary>
    ///     Rewrites collection navigations into subqueries, e.g.:
    ///     customers.Select(c => c.Order.OrderDetails.Where(...)) => customers.Select(c => orderDetails.Where(od => od.OrderId == c.Order.Id).Where(...))
    /// </summary>
    public class CollectionNavigationRewritingVisitor : ExpressionVisitor
    {
        private ParameterExpression _sourceParameter;
        private MethodInfo _listExistsMethodInfo = typeof(List<>).GetMethods().Where(m => m.Name == nameof(List<int>.Exists)).Single();

        public CollectionNavigationRewritingVisitor(ParameterExpression sourceParameter)
        {
            _sourceParameter = sourceParameter;
        }

        protected override Expression VisitLambda<T>(Expression<T> lambdaExpression)
        {
            var newBody = Visit(lambdaExpression.Body);

            return newBody != lambdaExpression.Body
                ? Expression.Lambda(newBody, lambdaExpression.Parameters)
                : lambdaExpression;
        }

        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            // don't touch Include
            // this is temporary, new nav expansion happens to early at the moment
            if (methodCallExpression.IsIncludeMethod())
            {
                return methodCallExpression;
            }

            if (methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.QueryableSelectManyMethodInfo))
            {
                return methodCallExpression;
            }

            if (methodCallExpression.Method.MethodIsClosedFormOf(LinqMethodHelpers.QueryableSelectManyWithResultOperatorMethodInfo))
            {
                var newResultSelector = Visit(methodCallExpression.Arguments[2]);

                return newResultSelector != methodCallExpression.Arguments[2]
                    ? methodCallExpression.Update(methodCallExpression.Object, new[] { methodCallExpression.Arguments[0], methodCallExpression.Arguments[1], newResultSelector })
                    : methodCallExpression;
            }

            // collection.Exists(predicate) -> Enumerable.Any(collection, predicate)
            if (methodCallExpression.Method.Name == nameof(List<int>.Exists)
                && methodCallExpression.Method.DeclaringType.IsGenericType
                && methodCallExpression.Method.DeclaringType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var newCaller = RemoveMaterializeCollectionNavigationMethodCall(Visit(methodCallExpression.Object));
                var newPredicate = Visit(methodCallExpression.Arguments[0]);

                return Expression.Call(
                    LinqMethodHelpers.EnumerableAnyPredicateMethodInfo.MakeGenericMethod(newCaller.Type.GetSequenceType()),
                    newCaller,
                    Expression.Lambda(
                        ((LambdaExpression)newPredicate).Body,
                        ((LambdaExpression)newPredicate).Parameters[0]));
            }

            // collection.Contains(element) -> Enumerable.Any(collection, c => c == element)
            if (methodCallExpression.Method.Name == nameof(List<int>.Contains)
                && methodCallExpression.Arguments.Count == 1
                && methodCallExpression.Object is NavigationBindingExpression navigationBindingCaller
                && navigationBindingCaller.NavigationTreeNode.Navigation != null
                && navigationBindingCaller.NavigationTreeNode.Navigation.IsCollection())
            {
                var newCaller = RemoveMaterializeCollectionNavigationMethodCall(Visit(methodCallExpression.Object));
                var newArgument = Visit(methodCallExpression.Arguments[0]);

                var lambdaParameter = Expression.Parameter(newCaller.Type.GetSequenceType(), newCaller.Type.GetSequenceType().GenerateParameterName());
                var lambda = Expression.Lambda(
                    Expression.Equal(lambdaParameter, newArgument),
                    lambdaParameter);

                return Expression.Call(
                    LinqMethodHelpers.EnumerableAnyPredicateMethodInfo.MakeGenericMethod(newCaller.Type.GetSequenceType()),
                    newCaller,
                    lambda);
            }

            var newObject = RemoveMaterializeCollectionNavigationMethodCall(Visit(methodCallExpression.Object));
            var newArguments = new List<Expression>();

            var argumentsChanged = false;
            foreach (var argument in methodCallExpression.Arguments)
            {
                var newArgument = RemoveMaterializeCollectionNavigationMethodCall(Visit(argument));
                newArguments.Add(newArgument);
                if (newArgument != argument)
                {
                    argumentsChanged = true;
                }
            }

            return newObject != methodCallExpression.Object || argumentsChanged
                ? methodCallExpression.Update(newObject, newArguments)
                : methodCallExpression;
            //return base.VisitMethodCall(methodCallExpression);
        }

        private Expression RemoveMaterializeCollectionNavigationMethodCall(Expression expression)
            => expression is MethodCallExpression methodCallExpression
                && methodCallExpression.Method.MethodIsClosedFormOf(NavigationExpansionHelpers.MaterializeCollectionNavigationMethodInfo)
            ? methodCallExpression.Arguments[0]
            : expression;

        public static Expression CreateCollectionNavigationExpression(NavigationTreeNode navigationTreeNode, ParameterExpression rootParameter, SourceMapping sourceMapping)
        {
            var collectionNavigationElementType = navigationTreeNode.Navigation.ForeignKey.DeclaringEntityType.ClrType;
            var entityQueryable = (Expression)NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(collectionNavigationElementType);

            // constantExpression.Value.GetType().GetSequenceType();
            var entityType = navigationTreeNode.Navigation.ForeignKey.DeclaringEntityType;// _model.FindEntityType(elementType);

            var newSourceMapping = new SourceMapping
            {
                RootEntityType = entityType,
            };

            var navigationTreeRoot = NavigationTreeNode.CreateRoot(newSourceMapping, fromMapping: new List<string>(), optional: false);

            // this is needed for cases like: root.Include(r => r.Collection).ThenInclude(c => c.Reference).Select(r => r.Collection)
            // result should be elements of the collection navigation with their 'Reference' included
            CopyIncludeInformation(navigationTreeNode, navigationTreeRoot, newSourceMapping);

            // TODO: this is copied from visit constant on NavigationExpandingVisitor - DRY!!!
            newSourceMapping.NavigationTree = navigationTreeRoot;

            var pendingSelectorParameter = Expression.Parameter(entityType.ClrType);
            var pendingSelector = Expression.Lambda(
                new NavigationBindingExpression(
                    pendingSelectorParameter,
                    navigationTreeRoot,
                    entityType,
                    newSourceMapping,
                    pendingSelectorParameter.Type),
                pendingSelectorParameter);

            entityQueryable = new NavigationExpansionRootExpression(
                new NavigationExpansionExpression(
                    entityQueryable,
                    new NavigationExpansionExpressionState(
                        pendingSelectorParameter,
                        new List<SourceMapping> { newSourceMapping },
                        pendingSelector,
                        applyPendingSelector: false,
                        new List<(MethodInfo method, LambdaExpression keySelector)>(),
                        pendingIncludeChain: null,
                        pendingCardinalityReducingOperator: null,
                        new List<List<string>>(),
                        materializeCollectionNavigation: null
                        /*new List<NestedExpansionMapping>()*/),
                    entityQueryable.Type),
                new List<string>(),
                entityQueryable.Type);


            //entityQueryable/*var result*/ =
            //    new CollectionNavigationExpansionRootExpression(
            //        new NavigationExpansionExpression(
            //        entityQueryable,
            //        new NavigationExpansionExpressionState(
            //            pendingSelectorParameter,
            //            new List<SourceMapping> { sourceMapping },
            //            pendingSelector,
            //            applyPendingSelector: false,
            //            new List<(MethodInfo method, LambdaExpression keySelector)>(),
            //            pendingIncludeChain: null,
            //            pendingCardinalityReducingOperator: null,
            //            new List<List<string>>(),
            //            materializeCollectionNavigation: null
            //            /*new List<NestedExpansionMapping>()*/),
            //        entityQueryable.Type));

            // this doesn't really mean much, maybe add special value to this enum for collections?
            // we always expand them when we see them in a binding and never during the "regular" expand navigations
            navigationTreeNode.ExpansionMode = NavigationTreeNodeExpansionMode.Complete;
            //navigationBindingExpression.NavigationTreeNode.Parent.Children.Remove(navigationBindingExpression.NavigationTreeNode);

            //TODO: this could be other things too: EF.Property and maybe field
            var outerBinding = new NavigationBindingExpression(
            rootParameter,
            navigationTreeNode.Parent,
            //navigationBindingExpression.NavigationTreeNode.Navigation.GetTargetType() ?? navigationBindingExpression.SourceMapping.RootEntityType,
            navigationTreeNode.Navigation.DeclaringEntityType,
            sourceMapping,
            navigationTreeNode.Navigation.DeclaringEntityType.ClrType);

            var outerKeyAccess = NavigationExpansionHelpers.CreateKeyAccessExpression(
                outerBinding,
                navigationTreeNode.Navigation.ForeignKey.PrincipalKey.Properties,
                addNullCheck: outerBinding.NavigationTreeNode.Optional);

            var innerParameter = Expression.Parameter(collectionNavigationElementType, collectionNavigationElementType.GenerateParameterName());
            var innerKeyAccess = NavigationExpansionHelpers.CreateKeyAccessExpression(
                innerParameter,
                navigationTreeNode.Navigation.ForeignKey.Properties);

            var predicate = Expression.Lambda(
                CreateKeyComparisonExpressionForCollectionNavigationSubquery(
                    outerKeyAccess,
                    innerKeyAccess,
                    outerBinding/*,
                            navigationBindingExpression.RootParameter,
                            // TODO: this is hacky
                            navigationBindingExpression.NavigationTreeNode.NavigationChain()*/),
                innerParameter);

            //predicate = (LambdaExpression)new NavigationPropertyUnbindingBindingExpressionVisitor(navigationBindingExpression.RootParameter).Visit(predicate);

            var result = Expression.Call(
                LinqMethodHelpers.QueryableWhereMethodInfo.MakeGenericMethod(collectionNavigationElementType),
                entityQueryable,
                predicate);

            return result;
        }

        public static Expression CreateCollectionNavigationExpression2(NavigationTreeNode navigationTreeNode, ParameterExpression rootParameter, SourceMapping sourceMapping)
        {
            var collectionEntityType = navigationTreeNode.Navigation.ForeignKey.DeclaringEntityType;
            //var collectionNavigationElementType = navigationTreeNode.Navigation.ForeignKey.DeclaringEntityType.ClrType;
            var entityQueryable = (Expression)NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(collectionEntityType.ClrType);

            //TODO: this could be other things too: EF.Property and maybe field
            var outerBinding = new NavigationBindingExpression(
            rootParameter,
            navigationTreeNode.Parent,
            //navigationBindingExpression.NavigationTreeNode.Navigation.GetTargetType() ?? navigationBindingExpression.SourceMapping.RootEntityType,
            navigationTreeNode.Navigation.DeclaringEntityType,
            sourceMapping,
            navigationTreeNode.Navigation.DeclaringEntityType.ClrType);

            var outerKeyAccess = NavigationExpansionHelpers.CreateKeyAccessExpression(
                outerBinding,
                navigationTreeNode.Navigation.ForeignKey.PrincipalKey.Properties,
                addNullCheck: outerBinding.NavigationTreeNode.Optional);

            var collectionCurrentParameter = Expression.Parameter(collectionEntityType.ClrType, collectionEntityType.ClrType.GenerateParameterName()); //     Expression.Parameter(entityType.ClrType);

            //var innerParameter = Expression.Parameter(collectionNavigationElementType, collectionNavigationElementType.GenerateParameterName());
            var innerKeyAccess = NavigationExpansionHelpers.CreateKeyAccessExpression(
                collectionCurrentParameter,
                navigationTreeNode.Navigation.ForeignKey.Properties);

            var predicate = Expression.Lambda(
                CreateKeyComparisonExpressionForCollectionNavigationSubquery(
                    outerKeyAccess,
                    innerKeyAccess,
                    outerBinding/*,
                            navigationBindingExpression.RootParameter,
                            // TODO: this is hacky
                            navigationBindingExpression.NavigationTreeNode.NavigationChain()*/),
                collectionCurrentParameter);

            var operand = Expression.Call(
                LinqMethodHelpers.QueryableWhereMethodInfo.MakeGenericMethod(collectionEntityType.ClrType),
                entityQueryable,
                predicate);

            var materializedJustForResultType = Expression.Call(
                NavigationExpansionHelpers.MaterializeCollectionNavigationMethodInfo.MakeGenericMethod(
                    navigationTreeNode.Navigation.GetTargetType().ClrType),
                operand,
                Expression.Constant(navigationTreeNode.Navigation));








            // constantExpression.Value.GetType().GetSequenceType();
            //var entityType = navigationTreeNode.Navigation.ForeignKey.DeclaringEntityType;// _model.FindEntityType(elementType);

            var newSourceMapping = new SourceMapping
            {
                RootEntityType = collectionEntityType,
            };

            var navigationTreeRoot = NavigationTreeNode.CreateRoot(newSourceMapping, fromMapping: new List<string>(), optional: false);

            // this is needed for cases like: root.Include(r => r.Collection).ThenInclude(c => c.Reference).Select(r => r.Collection)
            // result should be elements of the collection navigation with their 'Reference' included
            CopyIncludeInformation(navigationTreeNode, navigationTreeRoot, newSourceMapping);

            // TODO: this is copied from visit constant on NavigationExpandingVisitor - DRY!!!
            newSourceMapping.NavigationTree = navigationTreeRoot;

            var pendingSelectorParameter = Expression.Parameter(collectionEntityType.ClrType);
            var pendingSelector = Expression.Lambda(
                new NavigationBindingExpression(
                    pendingSelectorParameter,
                    navigationTreeRoot,
                    collectionEntityType,
                    newSourceMapping,
                    pendingSelectorParameter.Type),
                pendingSelectorParameter);

            var result = new NavigationExpansionRootExpression(
                new NavigationExpansionExpression(
                    operand,
                    new NavigationExpansionExpressionState(
                        pendingSelectorParameter,
                        new List<SourceMapping> { newSourceMapping },
                        pendingSelector,
                        applyPendingSelector: false,
                        new List<(MethodInfo method, LambdaExpression keySelector)>(),
                        pendingIncludeChain: null,
                        pendingCardinalityReducingOperator: null,
                        new List<List<string>>(),
                        materializeCollectionNavigation: navigationTreeNode.Navigation //null
                        /*new List<NestedExpansionMapping>()*/),
                    materializedJustForResultType.Type),
                new List<string>(),
                operand.Type);


            //entityQueryable/*var result*/ =
            //    new CollectionNavigationExpansionRootExpression(
            //        new NavigationExpansionExpression(
            //        entityQueryable,
            //        new NavigationExpansionExpressionState(
            //            pendingSelectorParameter,
            //            new List<SourceMapping> { sourceMapping },
            //            pendingSelector,
            //            applyPendingSelector: false,
            //            new List<(MethodInfo method, LambdaExpression keySelector)>(),
            //            pendingIncludeChain: null,
            //            pendingCardinalityReducingOperator: null,
            //            new List<List<string>>(),
            //            materializeCollectionNavigation: null
            //            /*new List<NestedExpansionMapping>()*/),
            //        entityQueryable.Type));

            // this doesn't really mean much, maybe add special value to this enum for collections?
            // we always expand them when we see them in a binding and never during the "regular" expand navigations
            navigationTreeNode.ExpansionMode = NavigationTreeNodeExpansionMode.Complete;
            //navigationBindingExpression.NavigationTreeNode.Parent.Children.Remove(navigationBindingExpression.NavigationTreeNode);



            //predicate = (LambdaExpression)new NavigationPropertyUnbindingBindingExpressionVisitor(navigationBindingExpression.RootParameter).Visit(predicate);

            //var result = Expression.Call(
            //    LinqMethodHelpers.QueryableWhereMethodInfo.MakeGenericMethod(collectionNavigationElementType),
            //    entityQueryable,
            //    predicate);

            return result;
        }


        protected override Expression VisitExtension(Expression extensionExpression)
        {
            if (extensionExpression is NavigationBindingExpression navigationBindingExpression)
            {
                if (navigationBindingExpression.NavigationTreeNode.Parent != null
                    && navigationBindingExpression.NavigationTreeNode.Navigation is INavigation lastNavigation
                    && lastNavigation.IsCollection())
                {
                    var result = CreateCollectionNavigationExpression(navigationBindingExpression.NavigationTreeNode, navigationBindingExpression.RootParameter, navigationBindingExpression.SourceMapping);

                    //var collectionNavigationElementType = lastNavigation.ForeignKey.DeclaringEntityType.ClrType;
                    //var entityQueryable = (Expression)NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(collectionNavigationElementType);

                    //// constantExpression.Value.GetType().GetSequenceType();
                    //var entityType = lastNavigation.ForeignKey.DeclaringEntityType;// _model.FindEntityType(elementType);

                    //var sourceMapping = new SourceMapping
                    //{
                    //    RootEntityType = entityType,
                    //};

                    //var navigationTreeRoot = NavigationTreeNode.CreateRoot(sourceMapping, fromMapping: new List<string>(), optional: false);

                    //// this is needed for cases like: root.Include(r => r.Collection).ThenInclude(c => c.Reference).Select(r => r.Collection)
                    //// result should be elements of the collection navigation with their 'Reference' included
                    //CopyIncludeInformation(navigationBindingExpression.NavigationTreeNode, navigationTreeRoot, sourceMapping);

                    //// TODO: this is copied from visit constant on NavigationExpandingVisitor - DRY!!!
                    //sourceMapping.NavigationTree = navigationTreeRoot;

                    //var pendingSelectorParameter = Expression.Parameter(entityType.ClrType);
                    //var pendingSelector = Expression.Lambda(
                    //    new NavigationBindingExpression(
                    //        pendingSelectorParameter,
                    //        navigationTreeRoot,
                    //        entityType,
                    //        sourceMapping,
                    //        pendingSelectorParameter.Type),
                    //    pendingSelectorParameter);

                    //entityQueryable = new NavigationExpansionRootExpression(
                    //    new NavigationExpansionExpression(
                    //        entityQueryable,
                    //        new NavigationExpansionExpressionState(
                    //            pendingSelectorParameter,
                    //            new List<SourceMapping> { sourceMapping },
                    //            pendingSelector,
                    //            applyPendingSelector: false,
                    //            new List<(MethodInfo method, LambdaExpression keySelector)>(),
                    //            pendingIncludeChain: null,
                    //            pendingCardinalityReducingOperator: null,
                    //            new List<List<string>>(),
                    //            materializeCollectionNavigation: null
                    //            /*new List<NestedExpansionMapping>()*/),
                    //        entityQueryable.Type),
                    //    new List<string>(),
                    //    entityQueryable.Type);

                    ////entityQueryable/*var result*/ =
                    ////    new CollectionNavigationExpansionRootExpression(
                    ////        new NavigationExpansionExpression(
                    ////        entityQueryable,
                    ////        new NavigationExpansionExpressionState(
                    ////            pendingSelectorParameter,
                    ////            new List<SourceMapping> { sourceMapping },
                    ////            pendingSelector,
                    ////            applyPendingSelector: false,
                    ////            new List<(MethodInfo method, LambdaExpression keySelector)>(),
                    ////            pendingIncludeChain: null,
                    ////            pendingCardinalityReducingOperator: null,
                    ////            new List<List<string>>(),
                    ////            materializeCollectionNavigation: null
                    ////            /*new List<NestedExpansionMapping>()*/),
                    ////        entityQueryable.Type));

                    //// this doesn't really mean much, maybe add special value to this enum for collections?
                    //// we always expand them when we see them in a binding and never during the "regular" expand navigations
                    //navigationBindingExpression.NavigationTreeNode.ExpansionMode = NavigationTreeNodeExpansionMode.Complete;
                    ////navigationBindingExpression.NavigationTreeNode.Parent.Children.Remove(navigationBindingExpression.NavigationTreeNode);

                    ////TODO: this could be other things too: EF.Property and maybe field
                    //var outerBinding = new NavigationBindingExpression(
                    //navigationBindingExpression.RootParameter,
                    //navigationBindingExpression.NavigationTreeNode.Parent,
                    ////navigationBindingExpression.NavigationTreeNode.Navigation.GetTargetType() ?? navigationBindingExpression.SourceMapping.RootEntityType,
                    //lastNavigation.DeclaringEntityType,
                    //navigationBindingExpression.SourceMapping,
                    //lastNavigation.DeclaringEntityType.ClrType);

                    //var outerKeyAccess = NavigationExpansionHelpers.CreateKeyAccessExpression(
                    //    outerBinding,
                    //    lastNavigation.ForeignKey.PrincipalKey.Properties,
                    //    addNullCheck: outerBinding.NavigationTreeNode.Optional);

                    //var innerParameter = Expression.Parameter(collectionNavigationElementType, collectionNavigationElementType.GenerateParameterName());
                    //var innerKeyAccess = NavigationExpansionHelpers.CreateKeyAccessExpression(
                    //    innerParameter,
                    //    lastNavigation.ForeignKey.Properties);

                    //var predicate = Expression.Lambda(
                    //    CreateKeyComparisonExpressionForCollectionNavigationSubquery(
                    //        outerKeyAccess,
                    //        innerKeyAccess,
                    //        outerBinding/*,
                    //        navigationBindingExpression.RootParameter,
                    //        // TODO: this is hacky
                    //        navigationBindingExpression.NavigationTreeNode.NavigationChain()*/),
                    //    innerParameter);

                    ////predicate = (LambdaExpression)new NavigationPropertyUnbindingBindingExpressionVisitor(navigationBindingExpression.RootParameter).Visit(predicate);

                    //var result = Expression.Call(
                    //    QueryableWhereMethodInfo.MakeGenericMethod(collectionNavigationElementType),
                    //    entityQueryable,
                    //    predicate);

                    return Expression.Call(
                        NavigationExpansionHelpers.MaterializeCollectionNavigationMethodInfo.MakeGenericMethod(result.Type.GetSequenceType()),
                        result,
                        Expression.Constant(lastNavigation));
                }
            }

            if (extensionExpression is CorrelationPredicateExpression correlationPredicateExpression)
            {
                var newOuterKeyNullCheck = Visit(correlationPredicateExpression.OuterKeyNullCheck);
                var newEqualExpression = (BinaryExpression)Visit(correlationPredicateExpression.EqualExpression);
                //var newNavigationRootExpression = Visit(nullSafeEqualExpression.NavigationRootExpression);

                if (newOuterKeyNullCheck != correlationPredicateExpression.OuterKeyNullCheck
                    || newEqualExpression != correlationPredicateExpression.EqualExpression
                    /*|| newNavigationRootExpression != nullSafeEqualExpression.NavigationRootExpression*/)
                {
                    return new CorrelationPredicateExpression(newOuterKeyNullCheck, newEqualExpression/*, newNavigationRootExpression, nullSafeEqualExpression.Navigations*/);
                }
            }

            //if (extensionExpression is NullSafeEqualExpression nullSafeEqualExpression)
            //{
            //    var newOuterKeyNullCheck = Visit(nullSafeEqualExpression.OuterKeyNullCheck);
            //    var newEqualExpression = (BinaryExpression)Visit(nullSafeEqualExpression.EqualExpression);
            //    var newNavigationRootExpression = Visit(nullSafeEqualExpression.NavigationRootExpression);

            //    if (newOuterKeyNullCheck != nullSafeEqualExpression.OuterKeyNullCheck
            //        || newEqualExpression != nullSafeEqualExpression.EqualExpression
            //        || newNavigationRootExpression != nullSafeEqualExpression.NavigationRootExpression)
            //    {
            //        return new NullSafeEqualExpression(newOuterKeyNullCheck, newEqualExpression, newNavigationRootExpression, nullSafeEqualExpression.Navigations);
            //    }
            //}

            if (extensionExpression is NavigationExpansionExpression nee)
            {
                var newOperand = Visit(nee.Operand);
                if (newOperand != nee.Operand)
                {
                    return new NavigationExpansionExpression(newOperand, nee.State, nee.Type);
                }
            }

            if (extensionExpression is NullConditionalExpression nullConditionalExpression)
            {
                var newCaller = Visit(nullConditionalExpression.Caller);
                var newAccessOperation = Visit(nullConditionalExpression.AccessOperation);

                return newCaller != nullConditionalExpression.Caller
                    || newAccessOperation != nullConditionalExpression.AccessOperation
                    ? new NullConditionalExpression(newCaller, newAccessOperation)
                    : nullConditionalExpression;
            }

            return extensionExpression;
        }

        private static void CopyIncludeInformation(NavigationTreeNode originalNavigationTree, NavigationTreeNode newNavigationTree, SourceMapping newSourceMapping)
        {
            foreach (var child in originalNavigationTree.Children.Where(n => n.Included == NavigationTreeNodeIncludeMode.Pending))
            {
                var copy = NavigationTreeNode.Create(newSourceMapping, child.Navigation, newNavigationTree, true);
                CopyIncludeInformation(child, copy, newSourceMapping);
            }
        }

        protected override Expression VisitMember(MemberExpression memberExpression)
        {
            //var newExpression = Visit(memberExpression.Expression);
            var newExpression = RemoveMaterializeCollectionNavigationMethodCall(Visit(memberExpression.Expression));
            if (newExpression != memberExpression.Expression)
            {
                //// unwrap MaterializeCollectionNavigation method call before applying the member access
                //// MaterializeCollectionNavigation is only needed on "naked" collections
                //if (newExpression is MethodCallExpression methodCallExpression
                //    && methodCallExpression.Method.MethodIsClosedFormOf(NavigationExpansionHelpers.MaterializeCollectionNavigationMethodInfo))
                //{
                //    newExpression = methodCallExpression.Arguments[0];
                //}

                if (memberExpression.Member.Name == nameof(List<int>.Count))
                {
                    //if (newExpression is MethodCallExpression methodCallExpression
                    //    && methodCallExpression.Method.MethodIsClosedFormOf(NavigationExpansionHelpers.MaterializeCollectionNavigationMethodInfo))
                    //{
                    //    newExpression = methodCallExpression.Arguments[0];
                    //}

                    var countMethod = LinqMethodHelpers.QueryableCountMethodInfo.MakeGenericMethod(newExpression.Type.GetSequenceType());
                    var result = Expression.Call(instance: null, countMethod, newExpression);

                    return result;
                }
                else
                {
                    return memberExpression.Update(newExpression);
                }
            }

            return memberExpression;
        }

        private static Expression CreateKeyComparisonExpressionForCollectionNavigationSubquery(
            Expression outerKeyExpression,
            Expression innerKeyExpression,
            Expression colectionRootExpression)
        {
            if (outerKeyExpression.Type != innerKeyExpression.Type)
            {
                if (outerKeyExpression.Type.IsNullableType())
                {
                    Debug.Assert(outerKeyExpression.Type.UnwrapNullableType() == innerKeyExpression.Type);

                    innerKeyExpression = Expression.Convert(innerKeyExpression, outerKeyExpression.Type);
                }
                else
                {
                    Debug.Assert(innerKeyExpression.Type.IsNullableType());
                    Debug.Assert(innerKeyExpression.Type.UnwrapNullableType() == outerKeyExpression.Type);

                    outerKeyExpression = Expression.Convert(outerKeyExpression, innerKeyExpression.Type);
                }
            }

            var outerNullProtection
                = Expression.NotEqual(
                    colectionRootExpression,
                    Expression.Constant(null, colectionRootExpression.Type));

            return new CorrelationPredicateExpression(
                outerNullProtection,
                Expression.Equal(outerKeyExpression, innerKeyExpression));
        }
    }
}

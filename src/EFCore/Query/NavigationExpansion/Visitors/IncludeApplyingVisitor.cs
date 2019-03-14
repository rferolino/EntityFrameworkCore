// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion.Visitors
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Linq.Expressions;
    using Microsoft.EntityFrameworkCore.Extensions.Internal;
    using Microsoft.EntityFrameworkCore.Metadata;
    using Microsoft.EntityFrameworkCore.Query.Internal;

    public class PendingSelectorIncludeRewriter : ExpressionVisitor
    {
        public PendingSelectorIncludeRewriter()
        {
        }

        // prune
        protected override Expression VisitMember(MemberExpression memberExpression) => memberExpression;

        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
            => methodCallExpression.Method.IsEFPropertyMethod()
            ? methodCallExpression
            : base.VisitMethodCall(methodCallExpression);

        protected override Expression VisitExtension(Expression extensionExpression)
        {
            if (extensionExpression is NavigationBindingExpression navigationBindingExpression)
            {
                var result = (Expression)navigationBindingExpression;
                //result = CreateIncludeCall(result, navigationBindingExpression.NavigationTreeNode, navigationBindingExpression.RootParameter, navigationBindingExpression.SourceMapping);

                foreach (var child in navigationBindingExpression.NavigationTreeNode.Children.Where(n => n.Included == NavigationTreeNodeIncludeMode.Pending))
                {
                    result = CreateIncludeCall(result, child, navigationBindingExpression.RootParameter, navigationBindingExpression.SourceMapping);
                    child.Included = NavigationTreeNodeIncludeMode.Complete;
                }

                return result;
            }

            if (extensionExpression is CustomRootExpression customRootExpression)
            {
                return customRootExpression;
            }

            if (extensionExpression is NavigationExpansionRootExpression expansionRootExpression)
            {
                return expansionRootExpression;
            }

            if (extensionExpression is NavigationExpansionExpression navigationExpansionExpression)
            {
                return navigationExpansionExpression;
            }

            return base.VisitExtension(extensionExpression);
        }

        private IncludeExpression CreateIncludeCall(Expression caller, NavigationTreeNode node, ParameterExpression rootParameter, SourceMapping sourceMapping)
            => node.Navigation.IsCollection()
            ? CreateIncludeCollectionCall(caller, node, rootParameter, sourceMapping)
            : CreateIncludeReferenceCall(caller, node, rootParameter, sourceMapping);

        private IncludeExpression CreateIncludeReferenceCall(Expression caller, NavigationTreeNode node, ParameterExpression rootParameter, SourceMapping sourceMapping)
        {
            // TODO: can navigation ever be null here?
            //var entityType = node.Navigation?.GetTargetType() ?? sourceMapping.RootEntityType;
            var entityType = node.Navigation.GetTargetType();
            var included = (Expression)new NavigationBindingExpression(rootParameter, node, entityType, sourceMapping, entityType.ClrType);

            foreach (var child in node.Children.Where(n => n.Included == NavigationTreeNodeIncludeMode.Pending))
            {
                included = CreateIncludeCall(included, child, rootParameter, sourceMapping);
                child.Included = NavigationTreeNodeIncludeMode.Complete;
            }

            return new IncludeExpression(caller, included, node.Navigation);
        }

        private IncludeExpression CreateIncludeCollectionCall(Expression caller, NavigationTreeNode node, ParameterExpression rootParameter, SourceMapping sourceMapping)
        {
            var included = CollectionNavigationRewritingVisitor.CreateCollectionNavigationExpression2(node, rootParameter, sourceMapping);

            //var entityType = node.Navigation.GetTargetType();

            //// TODO: copied from CollectionNavigationRewritingVisitor - DRY!

            //// TODO: which one is the correct entity type here???
            ////var collectionNavigationElementType = node.Navigation.ForeignKey.DeclaringEntityType.ClrType;
            //var collectionNavigationElementType = entityType.ClrType;
            //var entityQueryable = NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(collectionNavigationElementType);

            ////TODO: this could be other things too: EF.Property and maybe field
            //var outerBinding = new NavigationBindingExpression(
            //rootParameter,
            //node.Parent,
            //node.Navigation.DeclaringEntityType,
            //sourceMapping,
            //node.Navigation.DeclaringEntityType.ClrType);

            //var outerKeyAccess = NavigationExpansionHelpers.CreateKeyAccessExpression(
            //    outerBinding,
            //    node.Navigation.ForeignKey.PrincipalKey.Properties,
            //    addNullCheck: outerBinding.NavigationTreeNode.Optional);

            //var innerParameter = Expression.Parameter(collectionNavigationElementType, collectionNavigationElementType.GenerateParameterName());
            //var innerKeyAccess = NavigationExpansionHelpers.CreateKeyAccessExpression(
            //    innerParameter,
            //    node.Navigation.ForeignKey.Properties);

            //var predicate = Expression.Lambda(
            //    CreateKeyComparisonExpressionForCollectionNavigationSubquery(
            //        outerKeyAccess,
            //        innerKeyAccess,
            //        outerBinding/*,
            //            navigationBindingExpression.RootParameter,
            //            // TODO: this is hacky
            //            navigationBindingExpression.NavigationTreeNode.NavigationChain()*/),
            //    innerParameter);

            ////predicate = (LambdaExpression)new NavigationPropertyUnbindingBindingExpressionVisitor(navigationBindingExpression.RootParameter).Visit(predicate);

            //var included = Expression.Call(
            //    LinqMethodHelpers.QueryableWhereMethodInfo.MakeGenericMethod(collectionNavigationElementType),
            //    entityQueryable,
            //    predicate);

            //foreach (var child in node.Children.Where(n => n.Included == NavigationTreeNodeIncludeMode.Pending))
            //{
            //    included = Expression.Call(
            //        LinqMethodHelpers.QueryableSelectMethodInfo.MakeGenericMethod(collectionNavigationElementType, collectionNavigationElementType),
            //        CreateIncludeCall(included, child, innerParameter, sourceMapping),
            //        innerParameter);

            //    child.Included = NavigationTreeNodeIncludeMode.Complete;
            //}

            //included = Expression.Call(
            //    NavigationExpansionHelpers.MaterializeCollectionNavigationMethodInfo.MakeGenericMethod(
            //        node.Navigation.GetTargetType().ClrType),
            //    included,
            //    Expression.Constant(node.Navigation));

            //var result = Expression.Call(
            //    LinqMethodHelpers.QueryableWhereMethodInfo.MakeGenericMethod(collectionNavigationElementType),
            //    entityQueryable,
            //    predicate);

            //return Expression.Call(
            //    NavigationExpansionHelpers.MaterializeCollectionNavigationMethodInfo.MakeGenericMethod(result.Type.GetSequenceType()),
            //    result,
            //    Expression.Constant(lastNavigation));


            return new IncludeExpression(caller, included, node.Navigation);
        }

        // TODO: DRY!
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

    public class PendingIncludeFindingVisitor : ExpressionVisitor
    {
        public Dictionary<NavigationTreeNode, SourceMapping> PendingIncludes { get; } = new Dictionary<NavigationTreeNode, SourceMapping>();

        private void FindPendingReferenceIncludes(NavigationTreeNode node, SourceMapping sourceMapping)
        {
            if (node.Navigation != null && node.Navigation.IsCollection())
            {
                return;
            }

            if (node.Included == NavigationTreeNodeIncludeMode.Pending && node.ExpansionMode != NavigationTreeNodeExpansionMode.Complete)
            {
                PendingIncludes[node] = sourceMapping;
            }

            foreach (var child in node.Children)
            {
                FindPendingReferenceIncludes(child, sourceMapping);
            }
        }

        // prune
        protected override Expression VisitMember(MemberExpression memberExpression) => memberExpression;

        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
            => methodCallExpression.Method.IsEFPropertyMethod()
            ? methodCallExpression
            : base.VisitMethodCall(methodCallExpression);

        protected override Expression VisitExtension(Expression extensionExpression)
        {
            // TODO: what about nested scenarios i.e. NavigationExpansionExpression inside pending selector?
            if (extensionExpression is NavigationBindingExpression navigationBindingExpression)
            {
                // find all nodes and children UNTIL you find a collection in that subtree
                // collection navigations will be converted to their own NavigationExpansionExpressions and their children will be applied there
                FindPendingReferenceIncludes(navigationBindingExpression.NavigationTreeNode, navigationBindingExpression.SourceMapping);

                //foreach (var pendingInclude in navigationBindingExpression.NavigationTreeNode.Flatten().Where(n => n.Included == NavigationTreeNodeIncludeMode.Pending && n.ExpansionMode != NavigationTreeNodeExpansionMode.Complete && !n.Navigation.IsCollection()))
                //{
                //    PendingIncludes[pendingInclude] = navigationBindingExpression.SourceMapping;
                //}

                return navigationBindingExpression;
            }

            if (extensionExpression is CustomRootExpression customRootExpression)
            {
                return customRootExpression;
            }

            if (extensionExpression is NavigationExpansionRootExpression expansionRootExpression)
            {
                return expansionRootExpression;
            }

            if (extensionExpression is NavigationExpansionExpression navigationExpansionExpression)
            {
                return navigationExpansionExpression;
            }

            return base.VisitExtension(extensionExpression);
        }
    }

    //public class IncludeApplyingVisitor : LinqQueryVisitorBase
    //{
    //    protected override Expression VisitExtension(Expression extensionExpression)
    //    {
    //        if (extensionExpression is NavigationExpansionExpression navigationExpansionExpression)
    //        {
    //            var includeFinder = new PendingIncludeFindingVisitor();
    //            includeFinder.Visit(navigationExpansionExpression.State.PendingSelector);

    //            var includeRewriter = new PendingSelectorIncludeRewriter();
    //            var rewritten = (LambdaExpression)includeRewriter.Visit(navigationExpansionExpression.State.PendingSelector);
    //            navigationExpansionExpression.State.PendingSelector = rewritten;

    //            if (includeFinder.PendingIncludes.Count > 0)
    //            {
    //                var result = (source: navigationExpansionExpression.Operand, parameter: navigationExpansionExpression.State.CurrentParameter);

    //                foreach (var pendingIncludeNode in includeFinder.PendingIncludes)
    //                {
    //                    if (pendingIncludeNode.Key.Navigation.IsCollection())
    //                    {
    //                        throw new InvalidOperationException("Collections should not be part of the navigation tree: " + pendingIncludeNode.Key.Navigation);
    //                    }

    //                    result = AddNavigationJoin(
    //                        result.source,
    //                        result.parameter,
    //                        pendingIncludeNode.Value,
    //                        pendingIncludeNode.Key,
    //                        navigationExpansionExpression.State,
    //                        new List<INavigation>());
    //                }

    //                var pendingSelector = navigationExpansionExpression.State.PendingSelector;
    //                if (navigationExpansionExpression.State.CurrentParameter != result.parameter)
    //                {
    //                    var pendingSelectorBody = new ExpressionReplacingVisitor(navigationExpansionExpression.State.CurrentParameter, result.parameter).Visit(navigationExpansionExpression.State.PendingSelector.Body);
    //                    pendingSelector = Expression.Lambda(pendingSelectorBody, result.parameter);
    //                }

    //                var newState = new NavigationExpansionExpressionState(
    //                    result.parameter,
    //                    navigationExpansionExpression.State.SourceMappings,
    //                    pendingSelector,
    //                    applyPendingSelector: true,
    //                    navigationExpansionExpression.State.PendingOrderings,
    //                    navigationExpansionExpression.State.PendingIncludeChain,
    //                    navigationExpansionExpression.State.PendingCardinalityReducingOperator,
    //                    navigationExpansionExpression.State.CustomRootMappings,
    //                    navigationExpansionExpression.State.MaterializeCollectionNavigation);

    //                return new NavigationExpansionExpression(result.source, newState, navigationExpansionExpression.Type);
    //            }
    //            else
    //            {
    //                return navigationExpansionExpression;
    //            }
    //        }

    //        return base.VisitExtension(extensionExpression);
    //    }

    //    //TODO: DRY
    //    private (Expression source, ParameterExpression parameter) AddNavigationJoin(
    //        Expression sourceExpression,
    //        ParameterExpression parameterExpression,
    //        SourceMapping sourceMapping,
    //        NavigationTreeNode navigationTree,
    //        NavigationExpansionExpressionState state,
    //        List<INavigation> navigationPath)
    //    {
    //        if (navigationTree.ExpansionMode != NavigationTreeNodeExpansionMode.Complete)
    //        {
    //            // TODO: hack - if we wrapped collection around MaterializeCollectionNavigation during collection rewrite, unwrap that call when applying navigations on top
    //            if (sourceExpression is MethodCallExpression sourceMethodCall
    //                && sourceMethodCall.Method.Name == "MaterializeCollectionNavigation")
    //            {
    //                sourceExpression = sourceMethodCall.Arguments[1];
    //            }

    //            var navigation = navigationTree.Navigation;
    //            var sourceType = sourceExpression.Type.GetSequenceType();
    //            var navigationTargetEntityType = navigation.GetTargetType();

    //            var entityQueryable = NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(navigationTargetEntityType.ClrType);
    //            var resultType = typeof(TransparentIdentifier<,>).MakeGenericType(sourceType, navigationTargetEntityType.ClrType);

    //            var outerParameter = Expression.Parameter(sourceType, parameterExpression.Name);
    //            var outerKeySelectorParameter = outerParameter;
    //            var transparentIdentifierAccessorExpression = BuildTransparentIdentifierAccessorExpression(outerParameter, null, navigationTree.Parent.ToMapping);

    //            var outerKeySelectorBody = NavigationExpansionHelpers.CreateKeyAccessExpression(
    //                transparentIdentifierAccessorExpression,
    //                navigation.IsDependentToPrincipal()
    //                    ? navigation.ForeignKey.Properties
    //                    : navigation.ForeignKey.PrincipalKey.Properties,
    //                addNullCheck: navigationTree.Parent != null && navigationTree.Parent.Optional);

    //            var innerKeySelectorParameterType = navigationTargetEntityType.ClrType;
    //            var innerKeySelectorParameter = Expression.Parameter(
    //                innerKeySelectorParameterType,
    //                parameterExpression.Name + "." + navigationTree.Navigation.Name);

    //            var innerKeySelectorBody = NavigationExpansionHelpers.CreateKeyAccessExpression(
    //                innerKeySelectorParameter,
    //                navigation.IsDependentToPrincipal()
    //                    ? navigation.ForeignKey.PrincipalKey.Properties
    //                    : navigation.ForeignKey.Properties);

    //            if (outerKeySelectorBody.Type.IsNullableType()
    //                && !innerKeySelectorBody.Type.IsNullableType())
    //            {
    //                innerKeySelectorBody = Expression.Convert(innerKeySelectorBody, outerKeySelectorBody.Type);
    //            }
    //            else if (innerKeySelectorBody.Type.IsNullableType()
    //                && !outerKeySelectorBody.Type.IsNullableType())
    //            {
    //                outerKeySelectorBody = Expression.Convert(outerKeySelectorBody, innerKeySelectorBody.Type);
    //            }

    //            var outerKeySelector = Expression.Lambda(
    //                outerKeySelectorBody,
    //                outerKeySelectorParameter);

    //            var innerKeySelector = Expression.Lambda(
    //                innerKeySelectorBody,
    //                innerKeySelectorParameter);

    //            var oldParameterExpression = parameterExpression;
    //            if (navigationTree.Optional)
    //            {
    //                var groupingType = typeof(IEnumerable<>).MakeGenericType(navigationTargetEntityType.ClrType);
    //                var groupJoinResultType = typeof(TransparentIdentifier<,>).MakeGenericType(sourceType, groupingType);

    //                var groupJoinMethodInfo = QueryableGroupJoinMethodInfo.MakeGenericMethod(
    //                    sourceType,
    //                    navigationTargetEntityType.ClrType,
    //                    outerKeySelector.Body.Type,
    //                    groupJoinResultType);

    //                // TODO: massive hack!!!!
    //                if (sourceExpression.Type.ToString().StartsWith("System.Collections.Generic.IEnumerable`1[")
    //                    || sourceExpression.Type.ToString().StartsWith("System.Linq.IOrderedEnumerable`1["))
    //                {
    //                    groupJoinMethodInfo = EnumerableGroupJoinMethodInfo.MakeGenericMethod(
    //                    sourceType,
    //                    navigationTargetEntityType.ClrType,
    //                    outerKeySelector.Body.Type,
    //                    groupJoinResultType);
    //                }

    //                var resultSelectorOuterParameterName = outerParameter.Name;
    //                var resultSelectorOuterParameter = Expression.Parameter(sourceType, resultSelectorOuterParameterName);

    //                var resultSelectorInnerParameterName = innerKeySelectorParameter.Name;
    //                var resultSelectorInnerParameter = Expression.Parameter(groupingType, resultSelectorInnerParameterName);

    //                var groupJoinResultTransparentIdentifierCtorInfo
    //                    = groupJoinResultType.GetTypeInfo().GetConstructors().Single();

    //                var groupJoinResultSelector = Expression.Lambda(
    //                    Expression.New(groupJoinResultTransparentIdentifierCtorInfo, resultSelectorOuterParameter, resultSelectorInnerParameter),
    //                    resultSelectorOuterParameter,
    //                    resultSelectorInnerParameter);

    //                var groupJoinMethodCall
    //                    = Expression.Call(
    //                        groupJoinMethodInfo,
    //                        sourceExpression,
    //                        entityQueryable,
    //                        outerKeySelector,
    //                        innerKeySelector,
    //                        groupJoinResultSelector);

    //                var selectManyResultType = typeof(TransparentIdentifier<,>).MakeGenericType(groupJoinResultType, navigationTargetEntityType.ClrType);

    //                var selectManyMethodInfo = QueryableSelectManyWithResultOperatorMethodInfo.MakeGenericMethod(
    //                    groupJoinResultType,
    //                    navigationTargetEntityType.ClrType,
    //                    selectManyResultType);

    //                // TODO: massive hack!!!!
    //                if (groupJoinMethodCall.Type.ToString().StartsWith("System.Collections.Generic.IEnumerable`1[")
    //                    || groupJoinMethodCall.Type.ToString().StartsWith("System.Linq.IOrderedEnumerable`1["))
    //                {
    //                    selectManyMethodInfo = EnumerableSelectManyWithResultOperatorMethodInfo.MakeGenericMethod(
    //                        groupJoinResultType,
    //                        navigationTargetEntityType.ClrType,
    //                        selectManyResultType);
    //                }

    //                var defaultIfEmptyMethodInfo = EnumerableDefaultIfEmptyMethodInfo.MakeGenericMethod(navigationTargetEntityType.ClrType);

    //                var selectManyCollectionSelectorParameter = Expression.Parameter(groupJoinResultType);
    //                var selectManyCollectionSelector = Expression.Lambda(
    //                    Expression.Call(
    //                        defaultIfEmptyMethodInfo,
    //                        Expression.Field(selectManyCollectionSelectorParameter, nameof(TransparentIdentifier<object, object>.Inner))),
    //                    selectManyCollectionSelectorParameter);

    //                var selectManyResultTransparentIdentifierCtorInfo
    //                    = selectManyResultType.GetTypeInfo().GetConstructors().Single();

    //                // TODO: dont reuse parameters here?
    //                var selectManyResultSelector = Expression.Lambda(
    //                    Expression.New(selectManyResultTransparentIdentifierCtorInfo, selectManyCollectionSelectorParameter, innerKeySelectorParameter),
    //                    selectManyCollectionSelectorParameter,
    //                    innerKeySelectorParameter);

    //                var selectManyMethodCall
    //                    = Expression.Call(selectManyMethodInfo,
    //                    groupJoinMethodCall,
    //                    selectManyCollectionSelector,
    //                    selectManyResultSelector);

    //                sourceType = selectManyResultSelector.ReturnType;
    //                sourceExpression = selectManyMethodCall;

    //                var transparentIdentifierParameterName = resultSelectorInnerParameterName;
    //                var transparentIdentifierParameter = Expression.Parameter(selectManyResultSelector.ReturnType, transparentIdentifierParameterName);
    //                parameterExpression = transparentIdentifierParameter;
    //            }
    //            else
    //            {
    //                var joinMethodInfo = QueryableJoinMethodInfo.MakeGenericMethod(
    //                    sourceType,
    //                    navigationTargetEntityType.ClrType,
    //                    outerKeySelector.Body.Type,
    //                    resultType);

    //                // TODO: massive hack!!!!
    //                if (sourceExpression.Type.ToString().StartsWith("System.Collections.Generic.IEnumerable`1[")
    //                    || sourceExpression.Type.ToString().StartsWith("System.Linq.IOrderedEnumerable`1["))
    //                {
    //                    joinMethodInfo = EnumerableJoinMethodInfo.MakeGenericMethod(
    //                    sourceType,
    //                    navigationTargetEntityType.ClrType,
    //                    outerKeySelector.Body.Type,
    //                    resultType);
    //                }

    //                var resultSelectorOuterParameterName = outerParameter.Name;
    //                var resultSelectorOuterParameter = Expression.Parameter(sourceType, resultSelectorOuterParameterName);

    //                var resultSelectorInnerParameterName = innerKeySelectorParameter.Name;
    //                var resultSelectorInnerParameter = Expression.Parameter(navigationTargetEntityType.ClrType, resultSelectorInnerParameterName);

    //                var transparentIdentifierCtorInfo
    //                    = resultType.GetTypeInfo().GetConstructors().Single();

    //                var resultSelector = Expression.Lambda(
    //                    Expression.New(transparentIdentifierCtorInfo, resultSelectorOuterParameter, resultSelectorInnerParameter),
    //                    resultSelectorOuterParameter,
    //                    resultSelectorInnerParameter);

    //                var joinMethodCall = Expression.Call(
    //                    joinMethodInfo,
    //                    sourceExpression,
    //                    entityQueryable,
    //                    outerKeySelector,
    //                    innerKeySelector,
    //                    resultSelector);

    //                sourceType = resultSelector.ReturnType;
    //                sourceExpression = joinMethodCall;

    //                var transparentIdentifierParameterName = resultSelectorInnerParameterName;
    //                var transparentIdentifierParameter = Expression.Parameter(resultSelector.ReturnType, transparentIdentifierParameterName);
    //                parameterExpression = transparentIdentifierParameter;
    //            }

    //            // remap navigation 'To' paths -> for this navigation prepend "Inner", for every other (already expanded) navigation prepend "Outer"
    //            navigationTree.ToMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Inner));
    //            foreach (var mapping in state.SourceMappings)
    //            {
    //                foreach (var navigationTreeNode in mapping.NavigationTree.Flatten().Where(n => n.ExpansionMode == NavigationTreeNodeExpansionMode.Complete && n != navigationTree))
    //                {
    //                    navigationTreeNode.ToMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
    //                    if (navigationTree.Optional)
    //                    {
    //                        navigationTreeNode.ToMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
    //                    }
    //                }
    //            }

    //            foreach (var customRootMapping in state.CustomRootMappings)
    //            {
    //                customRootMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
    //                if (navigationTree.Optional)
    //                {
    //                    customRootMapping.Insert(0, nameof(TransparentIdentifier<object, object>.Outer));
    //                }
    //            }

    //            navigationTree.ExpansionMode = NavigationTreeNodeExpansionMode.Complete;
    //            navigationPath.Add(navigation);
    //        }
    //        else
    //        {
    //            navigationPath.Add(navigationTree.Navigation);
    //        }

    //        var result = (source: sourceExpression, parameter: parameterExpression);
    //        foreach (var child in navigationTree.Children)
    //        {
    //            result = AddNavigationJoin(
    //                result.source,
    //                result.parameter,
    //                sourceMapping,
    //                child,
    //                state,
    //                navigationPath.ToList());
    //        }

    //        return result;
    //    }

    //    // TODO: DRY
    //    private Expression BuildTransparentIdentifierAccessorExpression(Expression source, List<string> initialPath, List<string> accessorPath)
    //    {
    //        var result = source;

    //        var fullPath = initialPath != null
    //            ? initialPath.Concat(accessorPath).ToList()
    //            : accessorPath;

    //        if (fullPath != null)
    //        {
    //            foreach (var accessorPathElement in fullPath)
    //            {
    //                result = Expression.PropertyOrField(result, accessorPathElement);
    //            }
    //        }

    //        return result;
    //    }
    //}
}

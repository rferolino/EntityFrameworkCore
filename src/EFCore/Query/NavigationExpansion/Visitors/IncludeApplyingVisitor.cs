// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion.Visitors
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using Microsoft.EntityFrameworkCore.Extensions.Internal;

    public class PendingSelectorIncludeRewriter : ExpressionVisitor
    {
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

                foreach (var child in navigationBindingExpression.NavigationTreeNode.Children.Where(n => n.Included == NavigationTreeNodeIncludeMode.ReferencePending || n.Included == NavigationTreeNodeIncludeMode.Collection))
                {
                    result = CreateIncludeCall(result, child, navigationBindingExpression.RootParameter, navigationBindingExpression.SourceMapping);
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
            var entityType = node.Navigation.GetTargetType();
            var included = (Expression)new NavigationBindingExpression(rootParameter, node, entityType, sourceMapping, entityType.ClrType);

            foreach (var child in node.Children.Where(n => n.Included == NavigationTreeNodeIncludeMode.ReferencePending || n.Included == NavigationTreeNodeIncludeMode.Collection))
            {
                included = CreateIncludeCall(included, child, rootParameter, sourceMapping);
            }

            return new IncludeExpression(caller, included, node.Navigation);
        }

        private IncludeExpression CreateIncludeCollectionCall(Expression caller, NavigationTreeNode node, ParameterExpression rootParameter, SourceMapping sourceMapping)
        {
            var included = CollectionNavigationRewritingVisitor.CreateCollectionNavigationExpression(node, rootParameter, sourceMapping);

            return new IncludeExpression(caller, included, node.Navigation);
        }
    }

    public class PendingIncludeFindingVisitor : ExpressionVisitor
    {
        public virtual Dictionary<NavigationTreeNode, SourceMapping> PendingIncludes { get; } = new Dictionary<NavigationTreeNode, SourceMapping>();

        private void FindPendingReferenceIncludes(NavigationTreeNode node, SourceMapping sourceMapping)
        {
            if (node.Navigation != null && node.Navigation.IsCollection())
            {
                return;
            }

            if (node.Included == NavigationTreeNodeIncludeMode.ReferencePending && node.ExpansionMode != NavigationTreeNodeExpansionMode.ReferenceComplete)
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

        // prune
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
                // collection navigations will be converted to their own NavigationExpansionExpressions and their child includes will be applied when those NavigationExpansionExpressions are processed
                FindPendingReferenceIncludes(navigationBindingExpression.NavigationTreeNode, navigationBindingExpression.SourceMapping);

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
}

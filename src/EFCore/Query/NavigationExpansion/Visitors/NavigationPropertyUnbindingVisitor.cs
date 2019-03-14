// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Linq.Expressions;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion.Visitors
{
    class NavigationPropertyUnbindingVisitor : NavigationExpansionVisitorBase
    {
        private ParameterExpression _rootParameter;

        public NavigationPropertyUnbindingVisitor(ParameterExpression rootParameter)
        {
            _rootParameter = rootParameter;
        }

        protected override Expression VisitExtension(Expression extensionExpression)
        {
            if (extensionExpression is NavigationBindingExpression navigationBindingExpression
                && navigationBindingExpression.RootParameter == _rootParameter)
            {
                var result = navigationBindingExpression.RootParameter.BuildPropertyAccess(navigationBindingExpression.NavigationTreeNode.ToMapping);

                return result.Type != navigationBindingExpression.Type
                    ? Expression.Convert(result, navigationBindingExpression.Type)
                    : result;
            }

            if (extensionExpression is CustomRootExpression customRootExpression
                && customRootExpression.RootParameter == _rootParameter)
            {
                var result = _rootParameter.BuildPropertyAccess(customRootExpression.Mapping);

                return result.Type != customRootExpression.Type
                    ? Expression.Convert(result, customRootExpression.Type)
                    : result;
            }

            if (extensionExpression is NavigationExpansionRootExpression navigationExpansionRootExpression)
            {
                var result = navigationExpansionRootExpression.Unwrap();

                return new NavigationExpansionReducingVisitor().Visit(result);
            }

            if (extensionExpression is NavigationExpansionExpression navigationExpansionExpression)
            {
                var result = new NavigationExpansionReducingVisitor().Visit(navigationExpansionExpression);

                return result;
            }

            if (extensionExpression is IncludeExpression includeExpression)
            {
                var result = new NavigationExpansionReducingVisitor().Visit(includeExpression);

                return result;
            }

            return base.VisitExtension(extensionExpression);
        }
    }
}

// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Linq.Expressions;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion.Visitors
{
    public class NavigationExpansionReducingVisitor : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression extensionExpression)
        {
            if (extensionExpression is CorrelationPredicateExpression correlationPredicateExpression)
            {
                var newOuterKeyNullCheck = Visit(correlationPredicateExpression.OuterKeyNullCheck);
                var newEqualExpression = (BinaryExpression)Visit(correlationPredicateExpression.EqualExpression);

                return newOuterKeyNullCheck != correlationPredicateExpression.OuterKeyNullCheck || newEqualExpression != correlationPredicateExpression.EqualExpression
                    ? new CorrelationPredicateExpression(newOuterKeyNullCheck, newEqualExpression)
                    : correlationPredicateExpression;
            }

            if (extensionExpression is NavigationBindingExpression navigationBindingExpression)
            {
                var result = navigationBindingExpression.RootParameter.BuildPropertyAccess(navigationBindingExpression.NavigationTreeNode.ToMapping);
                //var result = navigationBindingExpression.NavigationTreeNode.BuildExpression(navigationBindingExpression.RootParameter);

                return result;
            }

            // TODO: temporary hack
            if (extensionExpression is IncludeExpression includeExpression)
            {
                var methodInfo = typeof(IncludeHelpers).GetMethod(nameof(IncludeHelpers.IncludeMethod))
                    .MakeGenericMethod(includeExpression.EntityExpression.Type);

                var newEntityExpression = Visit(includeExpression.EntityExpression);
                var newNavigationExpression = Visit(includeExpression.NavigationExpression);

                return Expression.Call(
                    methodInfo,
                    newEntityExpression,
                    newNavigationExpression,
                    Expression.Constant(includeExpression.Navigation));
            }

            if (extensionExpression is NavigationExpansionRootExpression navigationExpansionRootExpression)
            {
                return base.VisitExtension(navigationExpansionRootExpression.Unwrap());
            }

            return base.VisitExtension(extensionExpression);
        }
    }
}

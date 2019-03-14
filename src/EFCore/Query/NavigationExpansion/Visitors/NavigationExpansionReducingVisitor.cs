// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

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

            if (extensionExpression is NavigationBindingExpression navigationBindingExpression)
            {
                var result = navigationBindingExpression.NavigationTreeNode.BuildExpression(navigationBindingExpression.RootParameter);

                return result;
            }

            // TODO: temporary hack
            if (extensionExpression is IncludeExpression includeExpression)
            {
                var methodInfo = typeof(NavigationExpansionReducingVisitor).GetMethod(nameof(IncludeMethod)).MakeGenericMethod(includeExpression.EntityExpression.Type);
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

        public static TEntity IncludeMethod<TEntity>(TEntity entity, object includedNavigation, INavigation navigation)
        {
            if (entity == null)
            {
                return entity;
            }

            if (navigation.IsCollection() && includedNavigation == null)
            {
                return entity;
            }

            var propertyInfo = typeof(TEntity).GetProperty(navigation.Name);
            propertyInfo.SetValue(entity, includedNavigation);

            return entity;
        }
    }
}

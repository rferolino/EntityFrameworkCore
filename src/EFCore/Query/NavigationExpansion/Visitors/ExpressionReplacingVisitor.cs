// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Linq.Expressions;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion.Visitors
{
    public class ExpressionReplacingVisitor : NavigationExpansionVisitorBase
    {
        private Expression _searchedFor;
        private Expression _replaceWith;

        public ExpressionReplacingVisitor(Expression searchedFor, Expression replaceWith)
        {
            _searchedFor = searchedFor;
            _replaceWith = replaceWith;
        }

        public override Expression Visit(Expression expression)
            => expression == _searchedFor
            ? _replaceWith
            : base.Visit(expression);
    }
}

// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.Expressions.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion
{
    public class IncludeExpression : Expression, IPrintable
    {
        public IncludeExpression(Expression entityExpression, Expression navigationExpression, INavigation navigation)
        {
            EntityExpression = entityExpression;
            NavigationExpression = navigationExpression;
            Navigation = navigation;
            Type = EntityExpression.Type;
        }

        public Expression EntityExpression { get; set; }
        public Expression NavigationExpression { get; set; }
        public INavigation Navigation { get; set; }

        public override ExpressionType NodeType => ExpressionType.Extension;
        public override bool CanReduce => false;
        public override Type Type { get; }

        public void Print([NotNull] ExpressionPrinter expressionPrinter)
        {
            //expressionPrinter.StringBuilder.AppendLine("Include(");
            //expressionPrinter.StringBuilder.IncrementIndent();
            //expressionPrinter.Visit(EntityExpression);
            //expressionPrinter.StringBuilder.AppendLine(", ");
            //expressionPrinter.Visit(NavigationExpression);
            //expressionPrinter.StringBuilder.AppendLine(")");
            //expressionPrinter.StringBuilder.DecrementIndent();

            expressionPrinter.StringBuilder.Append("Include(");
            expressionPrinter.Visit(EntityExpression);
            expressionPrinter.StringBuilder.Append(".");
            expressionPrinter.Visit(NavigationExpression);
            expressionPrinter.StringBuilder.Append(")");
        }
    }
}

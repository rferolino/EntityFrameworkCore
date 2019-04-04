
// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query.Expressions.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion
{
    public class NavigationExpansionRootExpression : Expression, IPrintable
    {
        public NavigationExpansionExpression NavigationExpansion { get; }
        public List<string> Mapping { get; }
        public override ExpressionType NodeType => ExpressionType.Extension;
        public override bool CanReduce => false;
        public override Type Type => NavigationExpansion.Type;

        public NavigationExpansionRootExpression(NavigationExpansionExpression navigationExpansion, List<string> mapping)
        {
            NavigationExpansion = navigationExpansion;
            Mapping = mapping;
        }

        // TODO: HACK!!!
        public Expression Unwrap()
        {
            if (Mapping.Count == 0)
            {
                return NavigationExpansion;
            }

            var newOperand = NavigationExpansion.Operand;
            foreach (var mappingElement in Mapping)
            {
                newOperand = PropertyOrField(newOperand, mappingElement);
            }

            return new NavigationExpansionExpression(newOperand, NavigationExpansion.State, NavigationExpansion.Type);
        }

        public void Print([NotNull] ExpressionPrinter expressionPrinter)
        {
            expressionPrinter.StringBuilder.Append("EXPANSION_ROOT([" + Type.ShortDisplayName() + "] | ");
            expressionPrinter.Visit(Unwrap());
            expressionPrinter.StringBuilder.Append(")");
        }
    }
}




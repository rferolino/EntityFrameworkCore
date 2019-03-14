// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.Expressions.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion
{
    public class NavigationBindingExpression : Expression, IPrintable
    {
        public NavigationBindingExpression(
            ParameterExpression rootParameter,
            NavigationTreeNode navigationTreeNode,
            IEntityType entityType,
            SourceMapping sourceMapping,
            Type type)
        {
            RootParameter = rootParameter;
            NavigationTreeNode = navigationTreeNode;
            EntityType = entityType;
            SourceMapping = sourceMapping;
            Type = type;
        }

        public virtual ParameterExpression RootParameter { get; }
        public virtual IEntityType EntityType { get; }
        public virtual NavigationTreeNode NavigationTreeNode { get; }
        public virtual SourceMapping SourceMapping { get; }

        public override ExpressionType NodeType => ExpressionType.Extension;
        public override bool CanReduce => false;
        public override Type Type { get; }

        public virtual void Print(ExpressionPrinter expressionPrinter)
        {
            expressionPrinter.StringBuilder.Append("BINDING([" + EntityType.ClrType.ShortDisplayName() + "] | ");
            expressionPrinter.StringBuilder.Append(string.Join(".", NavigationTreeNode.FromMappings.First()) + " -> ");
            expressionPrinter.Visit(RootParameter);
            expressionPrinter.StringBuilder.Append(".");
            expressionPrinter.StringBuilder.Append(string.Join(".", NavigationTreeNode.ToMapping) + ")");
        }
    }
}

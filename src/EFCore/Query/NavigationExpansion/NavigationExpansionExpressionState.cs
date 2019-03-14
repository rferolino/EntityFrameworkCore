// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion
{
    public class NavigationExpansionExpressionState
    {
        public NavigationExpansionExpressionState(ParameterExpression currentParameter)
            : this(
                  currentParameter,
                  sourceMappings: new List<SourceMapping>(),
                  pendingSelector: null,
                  applyPendingSelector: false,
                  pendingOrderings: new List<(MethodInfo method, LambdaExpression keySelector)>(),
                  pendingIncludeChain: null,
                  pendingCardinalityReducingOperator: null,
                  customRootMappings: new List<List<string>>(),
                  materializeCollectionNavigation: null/*, new List<NestedExpansionMapping>()*/)
        {
        }

        public NavigationExpansionExpressionState(
            ParameterExpression currentParameter,
            List<SourceMapping> sourceMappings,
            LambdaExpression pendingSelector,
            bool applyPendingSelector,
            List<(MethodInfo method, LambdaExpression keySelector)> pendingOrderings,
            NavigationBindingExpression pendingIncludeChain,
            MethodInfo pendingCardinalityReducingOperator,
            List<List<string>> customRootMappings,
            INavigation materializeCollectionNavigation
            /*,
            List<NestedExpansionMapping> nestedExpansionMappings*/)
        {
            CurrentParameter = currentParameter;
            SourceMappings = sourceMappings;
            PendingSelector = pendingSelector;
            ApplyPendingSelector = applyPendingSelector;
            PendingOrderings = pendingOrderings;
            PendingIncludeChain = pendingIncludeChain;
            PendingCardinalityReducingOperator = pendingCardinalityReducingOperator;
            CustomRootMappings = customRootMappings;
            //NestedExpansionMappings = nestedExpansionMappings;
            MaterializeCollectionNavigation = materializeCollectionNavigation;
        }

        public ParameterExpression CurrentParameter { get; set; }
        public List<SourceMapping> SourceMappings { get; set; }
        public LambdaExpression PendingSelector { get; set; }
        public List<(MethodInfo method, LambdaExpression keySelector)> PendingOrderings { get; set; }
        public NavigationBindingExpression PendingIncludeChain { get; set; }
        public MethodInfo PendingCardinalityReducingOperator { get; set; }
        public bool ApplyPendingSelector { get; set; }
        public List<List<string>> CustomRootMappings { get; set; }
        public INavigation MaterializeCollectionNavigation { get; set; }

        //public List<NestedExpansionMapping> NestedExpansionMappings { get; set; }
    }
}

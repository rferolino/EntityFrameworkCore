// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore.Query.NavigationExpansion.Visitors;

namespace Microsoft.EntityFrameworkCore.Query.NavigationExpansion
{
    public static class ExpressionExtensions
    {
        public static LambdaExpression UnwrapQuote(this Expression expression)
            => expression is UnaryExpression unary && expression.NodeType == ExpressionType.Quote
            ? (LambdaExpression)unary.Operand
            : (LambdaExpression)expression;

        public static bool IsIncludeMethod(this MethodCallExpression methodCallExpression)
            => methodCallExpression.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)
                && methodCallExpression.Method.Name == nameof(EntityFrameworkQueryableExtensions.Include);

        //public static LambdaExpression CombineAndRemapLambdas(
        //    LambdaExpression first,
        //    LambdaExpression second)
        //    => CombineAndRemapLambdas(first, second, second.Parameters[0]);

        //public static LambdaExpression CombineAndRemapLambdas(
        //    LambdaExpression first,
        //    LambdaExpression second,
        //    ParameterExpression secondLambdaParameterToReplace)
        //{
        //    if (first == null)
        //    {
        //        return second;
        //    }

        //    if (second == null)
        //    {
        //        return first;
        //    }

        //    var lcev = new LambdaCombiningVisitor(first, first.Parameters[0], secondLambdaParameterToReplace);

        //    return (LambdaExpression)lcev.Visit(second);
        //}

        public static Expression CombineAndRemap(
            Expression source,
            ParameterExpression sourceParameter,
            Expression replaceWith)
        {
            if (source is LambdaExpression)
            {
                throw new InvalidOperationException("gfdgdfgdfgdf");
            }

            if (replaceWith is LambdaExpression)
            {
                throw new InvalidOperationException("gf");
            }

            var ecv = new ExpressionCombiningVisitor(sourceParameter, replaceWith);

            return ecv.Visit(source);
            //var lcev = new LambdaCombiningVisitor(first, first.Parameters[0], secondLambdaParameterToReplace);

            //return (LambdaExpression)lcev.Visit(second);
        }


        public class ExpressionCombiningVisitor : NavigationExpansionVisitorBase
        {
            private ParameterExpression _sourceParameter;
            private Expression _replaceWith;

            public ExpressionCombiningVisitor(
                ParameterExpression sourceParameter,
                Expression replaceWith)
            {
                _sourceParameter = sourceParameter;
                _replaceWith = replaceWith;
            }

            //protected override Expression VisitLambda<T>(Expression<T> lambdaExpression)
            //{
            //    // TODO: combine this with navigation replacing expression visitor? logic is the same
            //    var newParameters = new List<ParameterExpression>();
            //    var parameterChanged = false;

            //    foreach (var parameter in lambdaExpression.Parameters)
            //    {
            //        if (parameter == _previousParameter
            //            && parameter != _newParameter)
            //        {
            //            newParameters.Add(_newParameter);
            //            parameterChanged = true;
            //        }
            //        else
            //        {
            //            newParameters.Add(parameter);
            //        }
            //    }

            //    var newBody = Visit(lambdaExpression.Body);

            //    return parameterChanged || newBody != lambdaExpression.Body
            //        ? Expression.Lambda(newBody, newParameters)
            //        : lambdaExpression;
            //}

            protected override Expression VisitParameter(ParameterExpression parameterExpression)
                => parameterExpression == _sourceParameter
                ? _replaceWith
                : base.VisitParameter(parameterExpression);
            //{
            //    if (parameterExpression == _sourceParameter)
            //    {
            //        return 

            //        var prev = new ParameterReplacingExpressionVisitor(parameterToReplace: _sourceParameter, replaceWith: _replaceWith);
            //        var result = prev.Visit(parameterExpression);

            //        return result;
            //    }

            //    return base.VisitParameter(parameterExpression);
            //}

            protected override Expression VisitMember(MemberExpression memberExpression)
            {
                var newSource = Visit(memberExpression.Expression);
                if (newSource is NewExpression newExpression)
                {
                    var matchingMemberIndex = newExpression.Members.Select((m, i) => new { index = i, match = m == memberExpression.Member }).Where(r => r.match).SingleOrDefault()?.index;
                    if (matchingMemberIndex.HasValue)
                    {
                        return newExpression.Arguments[matchingMemberIndex.Value];
                    }
                }

                return newSource != memberExpression.Expression
                    ? memberExpression.Update(newSource)
                    : memberExpression;
            }

            //private class ParameterReplacingExpressionVisitor : ExpressionVisitor
            //{
            //    private ParameterExpression _parameterToReplace;
            //    private Expression _replaceWith;

            //    public ParameterReplacingExpressionVisitor(ParameterExpression parameterToReplace, Expression replaceWith)
            //    {
            //        _parameterToReplace = parameterToReplace;
            //        _replaceWith = replaceWith;
            //    }

            //    protected override Expression VisitLambda<T>(Expression<T> lambdaExpression)
            //    {
            //        var newBody = Visit(lambdaExpression.Body);

            //        return newBody != lambdaExpression.Body
            //            ? Expression.Lambda(newBody, lambdaExpression.Parameters)
            //            : lambdaExpression;
            //    }

            //    protected override Expression VisitParameter(ParameterExpression parameterExpression)
            //        => parameterExpression == _parameterToReplace
            //        ? _replaceWith
            //        : parameterExpression;
            //}
        }
    }

    // TODO: temporary hack
    public static class ParameterNamingExtensions
    {
        public static string GenerateParameterName(this Type type)
        {
            var sb = new StringBuilder();
            var removeLowerCase = sb.Append(type.Name.Where(c => char.IsUpper(c)).ToArray()).ToString();

            if (removeLowerCase.Length > 0)
            {
                return removeLowerCase.ToLower();
            }
            else
            {
                return type.Name.ToLower().Substring(0, 1);
            }
        }
    }
}

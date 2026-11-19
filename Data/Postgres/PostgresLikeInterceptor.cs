using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GitBench.Data.Postgres;

/// <summary>
/// <c>EF.Functions.Like</c> is case-insensitive on SQLite (for ASCII) but case-sensitive on PostgreSQL. GitBench's searches
/// (issues, GQL <c>~</c>, explore, wiki) rely on the SQLite behaviour, so on PostgreSQL every <c>Like</c> in a query is
/// rewritten to <c>ILike</c> before translation. One place, no provider checks at the call sites.
/// </summary>
public sealed class PostgresLikeInterceptor : IQueryExpressionInterceptor
{
    /// <summary>One shared instance: a new interceptor per options would give EF a new internal service provider every time.</summary>
    public static readonly PostgresLikeInterceptor Instance = new();

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData) =>
        new LikeToILike().Visit(queryExpression);

    private sealed class LikeToILike : ExpressionVisitor
    {
        private static readonly MethodInfo Like2 = typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like), [typeof(DbFunctions), typeof(string), typeof(string)])!;
        private static readonly MethodInfo Like3 = typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like), [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;
        private static readonly MethodInfo ILike2 = typeof(NpgsqlDbFunctionsExtensions).GetMethod(nameof(NpgsqlDbFunctionsExtensions.ILike), [typeof(DbFunctions), typeof(string), typeof(string)])!;
        private static readonly MethodInfo ILike3 = typeof(NpgsqlDbFunctionsExtensions).GetMethod(nameof(NpgsqlDbFunctionsExtensions.ILike), [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method == Like2) return Expression.Call(ILike2, node.Arguments.Select(Visit)!);
            if (node.Method == Like3) return Expression.Call(ILike3, node.Arguments.Select(Visit)!);
            return base.VisitMethodCall(node);
        }
    }
}

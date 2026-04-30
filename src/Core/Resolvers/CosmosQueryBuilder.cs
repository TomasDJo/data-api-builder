// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Models;

namespace Azure.DataApiBuilder.Core.Resolvers
{
    public class CosmosQueryBuilder : BaseSqlQueryBuilder
    {
        private readonly string _containerAlias = "c";

        // Cosmos NoSQL query language reserved keywords. When a JSON property
        // name matches one of these, dot notation (c.from) is rejected by the
        // service with a syntax error; bracket notation (c["from"]) must be used
        // instead. Matched case-insensitively.
        // Reference: https://learn.microsoft.com/azure/cosmos-db/nosql/query/keywords
        private static readonly HashSet<string> CosmosSqlReservedKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "ALL", "AND", "ANY", "ARRAY", "AS", "ASC", "AVG",
            "BETWEEN", "BY",
            "CASE", "CAST", "COLLATE", "COUNT", "CROSS",
            "DESC", "DISTINCT",
            "ELSE", "END", "EXCEPT", "EXISTS",
            "FALSE", "FROM", "FULL",
            "GROUP",
            "HAVING",
            "IN", "INNER", "INTERSECT", "INTO", "IS",
            "JOIN",
            "LEFT", "LIKE", "LIMIT",
            "MAX", "MIN",
            "NOT", "NULL",
            "OFFSET", "ON", "OR", "ORDER", "OUTER",
            "RIGHT",
            "SELECT", "SUM",
            "TABLE", "THEN", "TOP", "TRUE",
            "UNION",
            "UNDEFINED", "UNIQUE",
            "VALUE",
            "WHEN", "WHERE", "WITH"
        };

        /// <summary>
        /// Formats a JSON property access on the given alias, picking dot or
        /// bracket notation according to Cosmos NoSQL identifier rules. Bracket
        /// notation is required when the property name matches a reserved
        /// keyword, contains characters outside [A-Za-z0-9_], or starts with a
        /// digit.
        /// </summary>
        internal static string FormatPropertyAccess(string alias, string propertyName)
        {
            return RequiresBracketNotation(propertyName)
                ? $"{alias}[\"{propertyName.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"]"
                : $"{alias}.{propertyName}";
        }

        private static bool RequiresBracketNotation(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                return true;
            }

            if (CosmosSqlReservedKeywords.Contains(identifier))
            {
                return true;
            }

            char first = identifier[0];
            if (!char.IsLetter(first) && first != '_')
            {
                return true;
            }

            for (int i = 1; i < identifier.Length; i++)
            {
                char c = identifier[i];
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds a cosmos sql query string
        /// </summary>
        /// <param name="structure"></param>
        /// <returns></returns>
        public string Build(CosmosQueryStructure structure)
        {
            StringBuilder queryStringBuilder = new();
            queryStringBuilder.Append($"SELECT {WrappedColumns(structure)}"
                + $" FROM {_containerAlias}");
            string predicateString = Build(structure.Predicates);

            structure.DbPolicyPredicatesForOperations.TryGetValue(EntityActionOperation.Read, out string? policy);
            // If there is a predicate or policy, add a WHERE clause
            if (!string.IsNullOrEmpty(predicateString) || !string.IsNullOrEmpty(policy))
            {
                queryStringBuilder
                    .Append(" WHERE ")
                    .Append(string.IsNullOrEmpty(predicateString) || string.IsNullOrEmpty(policy)
                                ? predicateString + policy
                                : string.Join(" AND ", predicateString, policy));
            }

            if (structure.OrderByColumns.Count > 0)
            {
                queryStringBuilder.Append($" ORDER BY {Build(structure.OrderByColumns)}");
            }

            return queryStringBuilder.ToString();
        }

        protected override string Build(Column column)
        {
            string alias = _containerAlias;
            if (column.TableAlias != null)
            {
                alias = column.TableAlias;
            }

            return FormatPropertyAccess(alias, column.ColumnName);
        }

        protected override string Build(KeysetPaginationPredicate? predicate)
        {
            // Cosmos doesnt do keyset pagination
            return string.Empty;
        }

        public override string QuoteIdentifier(string ident)
        {
            return ident;
        }

        /// <summary>
        /// Build columns and wrap columns
        /// </summary>
        private string WrappedColumns(CosmosQueryStructure structure)
        {
            return string.Join(
                ", ",
                structure.Columns
                    .Select(c => FormatPropertyAccess(_containerAlias, c.Label))
                    .Distinct()
                );
        }

        /// <summary>
        /// Resolves a predicate operation enum to string
        /// </summary>
        protected override string Build(PredicateOperation op)
        {
            switch (op)
            {
                case PredicateOperation.Equal:
                    return "=";
                case PredicateOperation.GreaterThan:
                    return ">";
                case PredicateOperation.LessThan:
                    return "<";
                case PredicateOperation.GreaterThanOrEqual:
                    return ">=";
                case PredicateOperation.LessThanOrEqual:
                    return "<=";
                case PredicateOperation.NotEqual:
                    return "!=";
                case PredicateOperation.AND:
                    return "AND";
                case PredicateOperation.OR:
                    return "OR";
                case PredicateOperation.LIKE:
                    return "LIKE";
                case PredicateOperation.NOT_LIKE:
                    return "NOT LIKE";
                case PredicateOperation.IS:
                    return "";
                case PredicateOperation.IS_NOT:
                    return "NOT";
                case PredicateOperation.EXISTS:
                    return "EXISTS";
                case PredicateOperation.ARRAY_CONTAINS:
                    return "ARRAY_CONTAINS";
                case PredicateOperation.NOT_ARRAY_CONTAINS:
                    return "NOT ARRAY_CONTAINS";
                default:
                    throw new ArgumentException($"Cannot build unknown predicate operation {op}.");
            }
        }

        /// <summary>
        /// Build left and right predicate operand and resolve the predicate operator into
        /// {OperandLeft} {Operator} {OperandRight}
        /// </summary>
        protected override string Build(Predicate? predicate)
        {
            if (predicate is null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }

            string predicateString;
            if (predicate.Left is not null)
            {
                if (predicate.Op == PredicateOperation.ARRAY_CONTAINS || predicate.Op == PredicateOperation.NOT_ARRAY_CONTAINS)
                {
                    predicateString = $" {Build(predicate.Op)} ( {ResolveOperand(predicate.Left)}, {ResolveOperand(predicate.Right)})";
                }
                else if (TryBuildCaseInsensitive(predicate, out string? ciPredicate))
                {
                    predicateString = ciPredicate!;
                }
                else if (ResolveOperand(predicate.Right).Equals(GQLFilterParser.NullStringValue))
                {
                    // For Binary predicates:
                    predicateString = $" {Build(predicate.Op)} IS_NULL({ResolveOperand(predicate.Left)})";
                }
                else
                {
                    predicateString = $"{ResolveOperand(predicate.Left)} {Build(predicate.Op)} {ResolveOperand(predicate.Right)} ";
                }
            }
            else
            {
                // For Unary predicates, there is always a parenthesis around the operand.
                predicateString = $"{Build(predicate.Op)} ({ResolveOperand(predicate.Right)})";
            }

            if (predicate.AddParenthesis)
            {
                return "(" + predicateString + ")";
            }
            else
            {
                return predicateString;
            }
        }

        /// <summary>
        /// Emits Cosmos SQL for case-insensitive string operators using native
        /// built-ins (StringEquals, CONTAINS, STARTSWITH, ENDSWITH) with the
        /// optional ignoreCase=true argument. Returns false for any non-CI op.
        /// </summary>
        private bool TryBuildCaseInsensitive(Predicate predicate, out string? predicateString)
        {
            string left = ResolveOperand(predicate.Left);
            string right = ResolveOperand(predicate.Right);

            switch (predicate.Op)
            {
                case PredicateOperation.CI_STRING_EQUALS:
                    predicateString = $"StringEquals({left}, {right}, true)";
                    return true;
                case PredicateOperation.CI_NOT_STRING_EQUALS:
                    predicateString = $"NOT StringEquals({left}, {right}, true)";
                    return true;
                case PredicateOperation.CI_CONTAINS:
                    predicateString = $"CONTAINS({left}, {right}, true)";
                    return true;
                case PredicateOperation.CI_NOT_CONTAINS:
                    predicateString = $"NOT CONTAINS({left}, {right}, true)";
                    return true;
                case PredicateOperation.CI_STARTS_WITH:
                    predicateString = $"STARTSWITH({left}, {right}, true)";
                    return true;
                case PredicateOperation.CI_ENDS_WITH:
                    predicateString = $"ENDSWITH({left}, {right}, true)";
                    return true;
                default:
                    predicateString = null;
                    return false;
            }
        }

        /// <summary>
        /// Resolves the operand either as a column, another predicate,
        /// a SqlQueryStructure or returns it directly as string
        /// </summary>
        protected new string ResolveOperand(PredicateOperand? operand)
        {
            if (operand == null)
            {
                throw new ArgumentNullException(nameof(operand));
            }

            Column? column;
            string? stringType;
            Predicate? predicate;
            BaseQueryStructure? sqlQueryStructure;
            if ((column = operand.AsColumn()) != null)
            {
                return Build(column);
            }
            else if ((stringType = operand.AsString()) != null)
            {
                return stringType;
            }
            else if ((predicate = operand.AsPredicate()) != null)
            {
                return Build(predicate);
            }
            else if ((sqlQueryStructure = operand.AsCosmosQueryStructure()) is not null
                        && sqlQueryStructure is CosmosExistsQueryStructure cosmosExistsQueryStructure)
            {
                return Build(cosmosExistsQueryStructure);
            }
            else if ((sqlQueryStructure = operand.AsCosmosQueryStructure()) is not null
                        && sqlQueryStructure is CosmosQueryStructure cosmosQueryStructure)
            {
                return Build(cosmosQueryStructure);
            }
            else
            {
                throw new ArgumentException("Cannot get a value from PredicateOperand to build.");
            }
        }

        /// <inheritdoc />
        public virtual string Build(CosmosExistsQueryStructure structure)
        {
            string query = $"SELECT 1 " +
                   $"FROM {QuoteIdentifier(structure.SourceAlias)} IN {QuoteIdentifier(structure.DatabaseObject.SchemaName)} " +
                   $"WHERE {Build(structure.Predicates)}";

            return query;
        }

        /// <summary>
        /// Generate Cosmos DB Query for the given fromClause and predicates.
        /// </summary>
        /// <param name="fromClause">Use to generate FROM part in sql along with table and JOINS</param>
        /// <param name="predicates">Query Conditions</param>
        /// <returns>CosmosDB Exist Query</returns>
        public static string BuildExistsQueryForCosmos(string? fromClause, string? predicates)
        {
            string? existQuery = $"EXISTS " +
                                $"(SELECT VALUE 1 " +
                                    $"FROM {fromClause} ";
            if (!string.IsNullOrEmpty(predicates))
            {
                existQuery += $"WHERE {predicates})";
            }
            else
            {
                existQuery += ")";
            }

            return existQuery;
        }
    }
}

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Net;
using Azure.DataApiBuilder.Auth;
using Azure.DataApiBuilder.Config.DatabasePrimitives;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Models;
using Azure.DataApiBuilder.Core.Services;
using Azure.DataApiBuilder.Core.Services.MetadataProviders;
using Azure.DataApiBuilder.Service.Exceptions;
using Azure.DataApiBuilder.Service.GraphQLBuilder;
using Azure.DataApiBuilder.Service.GraphQLBuilder.GraphQLTypes;
using Azure.DataApiBuilder.Service.GraphQLBuilder.Queries;
using static Azure.DataApiBuilder.Service.GraphQLBuilder.Sql.SchemaConverter;
using HotChocolate.Execution.Processing;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using Microsoft.AspNetCore.Http;

namespace Azure.DataApiBuilder.Core.Resolvers
{
    public class CosmosQueryStructure : BaseQueryStructure
    {
        private readonly IMiddlewareContext _context;

        /// <summary>
        /// For any CosmosDB Query, the default alias for the container is 'c'
        /// </summary>
        public const string COSMOSDB_CONTAINER_DEFAULT_ALIAS = "c";

        private readonly string _containerAlias = COSMOSDB_CONTAINER_DEFAULT_ALIAS;
        public IncrementingInteger TableCounter { get; internal set; } = new();

        public override string SourceAlias { get => base.SourceAlias; set => base.SourceAlias = value; }

        public bool IsPaginated { get; internal set; }

        /// <summary>
        /// True when the client selected the `count` field on the *Connection
        /// return type. Triggers an extra `SELECT VALUE COUNT(1)` query alongside
        /// (or instead of) the items query.
        /// </summary>
        public bool IsCountRequested { get; internal set; }

        /// <summary>
        /// True when the client selected the `items` field on the *Connection
        /// return type. False means the caller only asked for metadata (count
        /// and/or pagination flags) and the items query can be skipped.
        /// </summary>
        public bool IsItemsRequested { get; internal set; }

        /// <summary>
        /// True when the client selected the `groupBy` field on the *Connection
        /// return type. The items query is replaced by an aggregate query built
        /// from <see cref="GroupByMetadata"/>.
        /// </summary>
        public bool IsGroupByRequested { get; internal set; }

        /// <summary>
        /// Grouping columns and aggregation operations parsed off the `groupBy`
        /// field. Empty unless <see cref="IsGroupByRequested"/> is true.
        /// </summary>
        public GroupByMetadata GroupByMetadata { get; private set; } = new();

        /// <summary>
        /// Cosmos caps a cross-partition GROUP BY at 21 aggregate system functions.
        /// Validated here so an over-large selection fails with a clear message
        /// instead of a generic service-side query error.
        /// </summary>
        private const int MAX_COSMOS_AGGREGATE_FUNCTIONS = 21;

        public string Container { get; internal set; }
        public string Database { get; internal set; }
        public string? Continuation { get; internal set; }
        public uint? MaxItemCount { get; internal set; }
        public string? PartitionKeyValue { get; internal set; }
        public List<OrderByColumn> OrderByColumns { get; internal set; }

        public RuntimeConfigProvider RuntimeConfigProvider { get; internal set; }

        public string GetTableAlias()
        {
            return $"table{TableCounter.Next()}";
        }

        /// <summary>
        /// Returns true if the given top-level field name appears as a direct
        /// selection on the supplied Connection field. Fragments are not
        /// traversed; count is always emitted as a plain leaf field by clients.
        /// </summary>
        private static bool HasSelection(FieldNode connectionField, string fieldName)
        {
            if (connectionField.SelectionSet is null)
            {
                return false;
            }

            foreach (ISelectionNode node in connectionField.SelectionSet.Selections)
            {
                if (node is FieldNode field && field.Name.Value == fieldName)
                {
                    return true;
                }
            }

            return false;
        }

        public CosmosQueryStructure(
            IMiddlewareContext context,
            IDictionary<string, object?> parameters,
            RuntimeConfigProvider provider,
            ISqlMetadataProvider metadataProvider,
            IAuthorizationResolver authorizationResolver,
            GQLFilterParser gQLFilterParser,
            IncrementingInteger? counter = null,
            List<Predicate>? predicates = null)
            : base(metadataProvider, authorizationResolver, gQLFilterParser, predicates: predicates, entityName: string.Empty, counter: counter)
        {
            _context = context;
            SourceAlias = _containerAlias;
            DatabaseObject.Name = _containerAlias;
            RuntimeConfigProvider = provider;
            Init(parameters);
        }

        /// <inheritdoc/>
        public override string MakeDbConnectionParam(object? value, string? columnName = null)
        {
            string encodedParamName = $"{PARAM_NAME_PREFIX}param{Counter.Next()}";
            Parameters.Add(encodedParamName, new(value));
            return encodedParamName;
        }

        private static IEnumerable<LabelledColumn> GenerateQueryColumns(SelectionSetNode selectionSet, DocumentNode document, string tableName)
        {
            foreach (ISelectionNode selectionNode in selectionSet.Selections)
            {
                if (selectionNode.Kind == SyntaxKind.FragmentSpread)
                {
                    FragmentSpreadNode fragmentSpread = (FragmentSpreadNode)selectionNode;
                    FragmentDefinitionNode fragmentDocumentNode = document.GetNodes()
                        .Where(n => n.Kind == SyntaxKind.FragmentDefinition)
                        .Cast<FragmentDefinitionNode>()
                        .Where(n => n.Name.Value == fragmentSpread.Name.Value)
                        .First();

                    foreach (LabelledColumn column in GenerateQueryColumns(fragmentDocumentNode.SelectionSet, document, tableName))
                    {
                        yield return column;
                    }
                }
                else if (selectionNode.Kind == SyntaxKind.InlineFragment)
                {
                    InlineFragmentNode inlineFragment = (InlineFragmentNode)selectionNode;
                    foreach (LabelledColumn column in GenerateQueryColumns(inlineFragment.SelectionSet, document, tableName))
                    {
                        yield return column;
                    }
                }
                else
                {
                    yield return new LabelledColumn(
                        tableSchema: string.Empty,
                        tableName: tableName,
                        columnName: string.Empty,
                        label: selectionNode.GetNodes().First().ToString());
                }
            }
        }

        [MemberNotNull(nameof(Container))]
        [MemberNotNull(nameof(Database))]
        [MemberNotNull(nameof(OrderByColumns))]
        private void Init(IDictionary<string, object?> queryParams)
        {
            ISelection selection = _context.Selection;
            ObjectType underlyingType = selection.Field.Type.NamedType<ObjectType>();

            IsPaginated = QueryBuilder.IsPaginationType(underlyingType);
            OrderByColumns = new();
            if (IsPaginated)
            {
                // ExtractQueryField returns the groupBy field in preference to items,
                // and rejects a query that selects both, so a non-null result here is
                // either the items field or the groupBy field.
                FieldNode? fieldNode = ExtractQueryField(selection.SyntaxNode);
                IsGroupByRequested = fieldNode?.Name.Value == QueryBuilder.GROUP_BY_FIELD_NAME;

                // Inspect the Connection selection set so we know which sub-queries
                // are actually needed. count is requested explicitly; items is the
                // default and is assumed when the caller asked for it OR when no
                // recognised sub-field was selected (e.g. only endCursor).
                IsCountRequested = HasSelection(selection.SyntaxNode, QueryBuilder.COUNT_FIELD_NAME);
                IsItemsRequested = fieldNode is not null && !IsGroupByRequested;

                ObjectType realType = underlyingType.Fields[QueryBuilder.PAGINATION_FIELD_NAME].Type.NamedType<ObjectType>();
                string entityName = MetadataProvider.GetEntityName(realType.Name);
                EntityName = entityName;
                Database = MetadataProvider.GetSchemaName(entityName);
                Container = MetadataProvider.GetDatabaseObjectName(entityName);

                // Entity name has to be resolved first: parsing groupBy runs field-level
                // authorization checks which are keyed on it.
                if (IsGroupByRequested)
                {
                    ProcessGroupByField(fieldNode!);
                }
                else if (fieldNode is not null)
                {
                    Columns.AddRange(GenerateQueryColumns(fieldNode.SelectionSet!, _context.Operation.Document, SourceAlias));
                }
            }
            else
            {
                Columns.AddRange(GenerateQueryColumns(selection.SyntaxNode.SelectionSet!, _context.Operation.Document, SourceAlias));
                string typeName = GraphQLUtils.TryExtractGraphQLFieldModelName(underlyingType.Directives, out string? modelName) ?
                    modelName :
                    underlyingType.Name;
                string entityName = MetadataProvider.GetEntityName(typeName);
                EntityName = entityName;
                Database = MetadataProvider.GetSchemaName(entityName);
                Container = MetadataProvider.GetDatabaseObjectName(entityName);
            }

            HttpContext httpContext = GraphQLFilterParser.GetHttpContextFromMiddlewareContext(_context);
            if (httpContext is not null)
            {
                AuthorizationPolicyHelpers.ProcessAuthorizationPolicies(
                    EntityActionOperation.Read,
                    this,
                    httpContext,
                    AuthorizationResolver,
                    (CosmosSqlMetadataProvider)MetadataProvider);
            }

            RuntimeConfigProvider.TryGetConfig(out RuntimeConfig? runtimeConfig);
            // first and after will not be part of query parameters. They will be going into headers instead.
            // TODO: Revisit 'first' while adding support for TOP queries
            if (queryParams.ContainsKey(QueryBuilder.PAGE_START_ARGUMENT_NAME))
            {
                object? firstArgument = queryParams[QueryBuilder.PAGE_START_ARGUMENT_NAME];
                MaxItemCount = runtimeConfig?.GetPaginationLimit((int?)firstArgument);

                queryParams.Remove(QueryBuilder.PAGE_START_ARGUMENT_NAME);
            }
            else
            {
                // set max item count to default value.
                MaxItemCount = runtimeConfig?.DefaultPageSize();
            }

            if (queryParams.ContainsKey(QueryBuilder.PAGINATION_TOKEN_ARGUMENT_NAME))
            {
                Continuation = (string?)queryParams[QueryBuilder.PAGINATION_TOKEN_ARGUMENT_NAME];
                queryParams.Remove(QueryBuilder.PAGINATION_TOKEN_ARGUMENT_NAME);
            }

            if (queryParams.ContainsKey(QueryBuilder.PARTITION_KEY_FIELD_NAME))
            {
                PartitionKeyValue = (string?)queryParams[QueryBuilder.PARTITION_KEY_FIELD_NAME];
                queryParams.Remove(QueryBuilder.PARTITION_KEY_FIELD_NAME);
            }

            if (queryParams.ContainsKey("orderBy"))
            {
                object? orderByObject = queryParams["orderBy"];

                if (orderByObject is not null)
                {
                    // Cosmos NoSQL cannot combine ORDER BY with GROUP BY. Fail with an
                    // explicit message rather than silently dropping the ordering the
                    // caller asked for.
                    if (IsGroupByRequested)
                    {
                        throw new DataApiBuilderException(
                            message: "orderBy is not supported together with groupBy for Cosmos DB NoSQL. Order the grouped results client-side.",
                            statusCode: HttpStatusCode.BadRequest,
                            subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest);
                    }

                    OrderByColumns = ProcessGraphQLOrderByArg((List<ObjectFieldNode>)orderByObject);
                }

                queryParams.Remove("orderBy");
            }

            if (queryParams.ContainsKey(QueryBuilder.FILTER_FIELD_NAME))
            {
                object? filterObject = queryParams[QueryBuilder.FILTER_FIELD_NAME];

                if (filterObject is not null)
                {
                    List<ObjectFieldNode> filterFields = (List<ObjectFieldNode>)filterObject;
                    Predicates.Add(
                        GraphQLFilterParser.Parse(
                            _context,
                            filterArgumentSchema: selection.Field.Arguments[QueryBuilder.FILTER_FIELD_NAME],
                            fields: filterFields,
                            queryStructure: this));

                    // after parsing all the GraphQL filters,
                    // reset the source alias and object name to the generic container alias
                    // since these may potentially be updated due to the presence of nested filters.
                    SourceAlias = _containerAlias;
                    DatabaseObject.Name = _containerAlias;
                }
            }
            else
            {
                foreach (KeyValuePair<string, object?> parameter in queryParams)
                {
                    Predicates.Add(new Predicate(
                        new PredicateOperand(new Column(tableSchema: string.Empty, _containerAlias, parameter.Key)),
                        PredicateOperation.Equal,
                        new PredicateOperand($"{MakeDbConnectionParam(parameter.Value)}")
                    ));
                }
            }
        }

        /// <summary>
        /// Parses the `groupBy` field on the *Connection type into <see cref="GroupByMetadata"/>.
        ///
        /// Example:
        /// groupBy(fields: [makeName]) {
        ///   fields { makeName }
        ///   aggregations { count(field: id) total: sum(field: weight) }
        /// }
        ///
        /// Mirrors SqlQueryStructure.ProcessGroupByField, but resolves columns against the
        /// Cosmos container alias and rejects the parts of the shared groupBy schema that
        /// Cosmos NoSQL cannot serve.
        /// </summary>
        private void ProcessGroupByField(FieldNode groupByField)
        {
            string roleOfGraphQLRequest = Authorization.AuthorizationResolver.GetRoleOfGraphQLRequest(_context);
            HashSet<string> fieldsInArgument = new();

            ArgumentNode? fieldsArg = groupByField.Arguments.FirstOrDefault(a => a.Name.Value == QueryBuilder.GROUP_BY_FIELDS_FIELD_NAME);

            if (fieldsArg is { Value: ListValueNode fieldsList })
            {
                foreach (EnumValueNode value in fieldsList.Items.Cast<EnumValueNode>())
                {
                    string fieldName = value.Value;
                    AuthorizeFieldOrThrow(fieldName, roleOfGraphQLRequest, DataApiBuilderException.GRAPHQL_GROUPBY_FIELD_AUTHZ_FAILURE);

                    GroupByMetadata.Fields[fieldName] = new Column(tableSchema: string.Empty, _containerAlias, fieldName);
                    fieldsInArgument.Add(fieldName);
                }
            }

            if (GroupByMetadata.Fields.Count == 0)
            {
                throw new DataApiBuilderException(
                    message: "groupBy requires at least one field in the 'fields' argument for Cosmos DB NoSQL.",
                    statusCode: HttpStatusCode.BadRequest,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest);
            }

            if (groupByField.SelectionSet is null)
            {
                return;
            }

            foreach (FieldNode field in groupByField.SelectionSet.Selections.Cast<FieldNode>())
            {
                switch (field.Name.Value)
                {
                    case QueryBuilder.GROUP_BY_FIELDS_FIELD_NAME:
                        GroupByMetadata.RequestedFields = true;
                        ValidateGroupByFieldSelections(field, fieldsInArgument);
                        break;

                    case QueryBuilder.GROUP_BY_AGGREGATE_FIELD_NAME:
                        GroupByMetadata.RequestedAggregations = true;
                        ProcessAggregations(field, roleOfGraphQLRequest);
                        break;
                }
            }
        }

        /// <summary>
        /// Every field selected under groupBy.fields must also appear in the groupBy
        /// argument - Cosmos, like SQL, can only project columns it grouped on.
        /// </summary>
        private static void ValidateGroupByFieldSelections(FieldNode groupByFieldSelection, HashSet<string> fieldsInArgument)
        {
            if (groupByFieldSelection.SelectionSet is null)
            {
                return;
            }

            foreach (ISelectionNode node in groupByFieldSelection.SelectionSet.Selections)
            {
                string fieldName = ((FieldNode)node).Name.Value;
                if (!fieldsInArgument.Contains(fieldName))
                {
                    throw new DataApiBuilderException(
                        message: "Groupby fields in selection must match the fields in the groupby argument.",
                        statusCode: HttpStatusCode.BadRequest,
                        subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest);
                }
            }
        }

        /// <summary>
        /// Parses the aggregation selections (max/min/avg/sum/count) into AggregationColumns.
        ///
        /// Two arguments the shared schema exposes cannot be honoured by Cosmos NoSQL and are
        /// rejected rather than silently ignored: `having` (Cosmos has no HAVING clause) and
        /// `distinct` (Cosmos has no DISTINCT inside aggregate functions).
        /// </summary>
        private void ProcessAggregations(FieldNode aggregationsField, string roleOfGraphQLRequest)
        {
            if (aggregationsField.SelectionSet is null)
            {
                return;
            }

            foreach (FieldNode field in aggregationsField.SelectionSet.Selections.Cast<FieldNode>())
            {
                ArgumentNode? fieldArg = field.Arguments
                    .FirstOrDefault(a => a.Name.Value == QueryBuilder.GROUP_BY_AGGREGATE_FIELD_ARG_NAME);

                if (fieldArg is null)
                {
                    continue;
                }

                AggregationType operation = Enum.Parse<AggregationType>(field.Name.Value);
                string fieldName = ((EnumValueNode)fieldArg.Value).Value;

                AuthorizeFieldOrThrow(
                    fieldName,
                    roleOfGraphQLRequest,
                    DataApiBuilderException.GRAPHQL_AGGREGATION_FIELD_AUTHZ_FAILURE,
                    operation.ToString());

                bool distinct = field.Arguments
                    .FirstOrDefault(a => a.Name.Value == QueryBuilder.GROUP_BY_AGGREGATE_FIELD_DISTINCT_NAME)
                    ?.Value.Value as bool? ?? false;

                if (distinct)
                {
                    throw new DataApiBuilderException(
                        message: $"The 'distinct' argument is not supported for Cosmos DB NoSQL aggregations. Remove it from '{field.Name.Value}'.",
                        statusCode: HttpStatusCode.BadRequest,
                        subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest);
                }

                if (field.Arguments.Any(a => a.Name.Value == QueryBuilder.GROUP_BY_AGGREGATE_FIELD_HAVING_NAME))
                {
                    throw new DataApiBuilderException(
                        message: $"The 'having' argument is not supported for Cosmos DB NoSQL aggregations. Cosmos has no HAVING clause; filter the grouped results client-side. Remove it from '{field.Name.Value}'.",
                        statusCode: HttpStatusCode.BadRequest,
                        subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest);
                }

                AggregationColumn column = new(
                    tableSchema: string.Empty,
                    tableName: _containerAlias,
                    columnName: fieldName,
                    type: operation,
                    alias: field.Alias?.Value ?? operation.ToString(),
                    distinct: false);

                GroupByMetadata.Aggregations.Add(new AggregationOperation(column));
            }

            if (GroupByMetadata.Aggregations.Count > MAX_COSMOS_AGGREGATE_FUNCTIONS)
            {
                throw new DataApiBuilderException(
                    message: $"A grouped Cosmos DB NoSQL query supports at most {MAX_COSMOS_AGGREGATE_FUNCTIONS} aggregate functions, but {GroupByMetadata.Aggregations.Count} were requested.",
                    statusCode: HttpStatusCode.BadRequest,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest);
            }
        }

        /// <summary>
        /// Throws when the role behind the request has no read access to the given field.
        /// </summary>
        private void AuthorizeFieldOrThrow(string fieldName, string roleOfGraphQLRequest, string messageTemplate, params object[] extraArgs)
        {
            IEnumerable<string> roles = AuthorizationResolver.GetRolesForField(EntityName, field: fieldName, operation: EntityActionOperation.Read);
            if (roles is not null && !roles.Contains(roleOfGraphQLRequest, StringComparer.OrdinalIgnoreCase))
            {
                object[] formatArgs = new object[] { fieldName }.Concat(extraArgs).ToArray();
                throw new DataApiBuilderException(
                    message: string.Format(messageTemplate, formatArgs),
                    statusCode: HttpStatusCode.Forbidden,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.AuthorizationCheckFailed);
            }
        }

        /// <summary>
        /// Create a list of orderBy columns from the orderBy argument
        /// passed to the gql query
        /// </summary>
        private List<OrderByColumn> ProcessGraphQLOrderByArg(List<ObjectFieldNode> orderByFields)
        {
            // Create list of primary key columns
            // we always have the primary keys in
            // the order by statement for the case
            // of tie breaking and pagination
            List<OrderByColumn> orderByColumnsList = new();

            foreach (ObjectFieldNode field in orderByFields)
            {
                if (field.Value is NullValueNode)
                {
                    continue;
                }

                string fieldName = field.Name.ToString();

                EnumValueNode enumValue = (EnumValueNode)field.Value;

                if (enumValue.Value == $"{OrderBy.DESC}")
                {
                    orderByColumnsList.Add(new OrderByColumn(tableSchema: string.Empty, _containerAlias, fieldName, direction: OrderBy.DESC));
                }
                else
                {
                    orderByColumnsList.Add(new OrderByColumn(tableSchema: string.Empty, _containerAlias, fieldName));
                }
            }

            return orderByColumnsList;
        }
    }
}

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.DataApiBuilder.Core.Resolvers;
using Azure.DataApiBuilder.Service.Tests.SqlTests;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QueryBuilder = Azure.DataApiBuilder.Service.GraphQLBuilder.Queries.QueryBuilder;

namespace Azure.DataApiBuilder.Service.Tests.CosmosTests
{
    /// <summary>
    /// Integration tests for the Connection-level `groupBy` field on Cosmos NoSQL entities.
    ///
    /// Seed data is 10 documents whose `name` cycles through a 9-element planet array, so
    /// exactly one name ("Earth") occurs twice and the rest occur once. `age` is the seed
    /// index 0..9 and `dimension` is the constant "space", which gives a single-group case,
    /// a multi-group case and predictable numeric aggregates.
    /// </summary>
    [TestClass, TestCategory(TestCategory.COSMOSDBNOSQL)]
    public class QueryGroupByTests : TestBase
    {
        private const string _graphQLQueryName = "planets";
        private const int _seededDocCount = 10;
        private const int _distinctPlanetNames = 9;

        [TestInitialize]
        public void TestFixtureSetup()
        {
            CosmosClientProvider cosmosClientProvider = _application.Services.GetService<CosmosClientProvider>();
            CosmosClient cosmosClient = cosmosClientProvider.Clients[cosmosClientProvider.RuntimeConfigProvider.GetConfig().DefaultDataSourceName];
            cosmosClient.CreateDatabaseIfNotExistsAsync(DATABASE_NAME).Wait();
            cosmosClient.GetDatabase(DATABASE_NAME).CreateContainerIfNotExistsAsync(_containerName, "/id").Wait();
            CreateItems(DATABASE_NAME, _containerName, _seededDocCount);
        }

        /// <summary>
        /// Grouping on a constant field collapses every document into one group, and the
        /// count aggregation over that group must equal the total document count.
        /// </summary>
        [TestMethod]
        public async Task TestGroupBySingleGroup()
        {
            string gqlQuery = @"{
                planets {
                    groupBy(fields: [dimension]) {
                        fields { dimension }
                        aggregations { count(field: id) }
                    }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            JsonElement groups = result.GetProperty(QueryBuilder.GROUP_BY_FIELD_NAME);

            Assert.AreEqual(1, groups.GetArrayLength(), "All seeded documents share the same dimension");
            JsonElement group = groups[0];
            Assert.AreEqual("space", group.GetProperty("fields").GetProperty("dimension").GetString());
            Assert.AreEqual(_seededDocCount, group.GetProperty("aggregations").GetProperty("count").GetInt32());
        }

        /// <summary>
        /// Grouping on the planet name yields one group per distinct name, and the duplicated
        /// name is counted twice - proving the counts come from the server-side GROUP BY and
        /// not from the number of returned rows.
        /// </summary>
        [TestMethod]
        public async Task TestGroupByProducesOneGroupPerDistinctValue()
        {
            string gqlQuery = @"{
                planets {
                    groupBy(fields: [name]) {
                        fields { name }
                        aggregations { count(field: id) }
                    }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            JsonElement groups = result.GetProperty(QueryBuilder.GROUP_BY_FIELD_NAME);

            Assert.AreEqual(_distinctPlanetNames, groups.GetArrayLength());

            Dictionary<string, int> countsByName = groups.EnumerateArray().ToDictionary(
                group => group.GetProperty("fields").GetProperty("name").GetString(),
                group => group.GetProperty("aggregations").GetProperty("count").GetInt32());

            Assert.AreEqual(2, countsByName["Earth"], "Earth is seeded twice by the round-robin");
            Assert.AreEqual(_seededDocCount, countsByName.Values.Sum(), "Group counts must sum to the document count");
        }

        /// <summary>
        /// All five aggregate operations over a single group, including an aliased one.
        /// age is the seed index, so over 10 documents: min 0, max 9, sum 45, avg 4.5.
        /// </summary>
        [TestMethod]
        public async Task TestGroupByAllAggregateOperations()
        {
            string gqlQuery = @"{
                planets {
                    groupBy(fields: [dimension]) {
                        aggregations {
                            min(field: age)
                            max(field: age)
                            sum(field: age)
                            avg(field: age)
                            total: count(field: age)
                        }
                    }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            JsonElement aggregations = result.GetProperty(QueryBuilder.GROUP_BY_FIELD_NAME)[0].GetProperty("aggregations");

            Assert.AreEqual(0d, aggregations.GetProperty("min").GetDouble());
            Assert.AreEqual(9d, aggregations.GetProperty("max").GetDouble());
            Assert.AreEqual(45d, aggregations.GetProperty("sum").GetDouble());
            Assert.AreEqual(4.5d, aggregations.GetProperty("avg").GetDouble());
            Assert.AreEqual(_seededDocCount, aggregations.GetProperty("total").GetInt32(), "GraphQL aliases must survive into the result");
        }

        /// <summary>
        /// Grouping on more than one field produces one group per distinct combination.
        /// dimension is constant, so the combination count matches the distinct name count.
        /// </summary>
        [TestMethod]
        public async Task TestGroupByMultipleFields()
        {
            string gqlQuery = @"{
                planets {
                    groupBy(fields: [dimension, name]) {
                        fields { dimension name }
                        aggregations { count(field: id) }
                    }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            JsonElement groups = result.GetProperty(QueryBuilder.GROUP_BY_FIELD_NAME);

            Assert.AreEqual(_distinctPlanetNames, groups.GetArrayLength());
            foreach (JsonElement group in groups.EnumerateArray())
            {
                Assert.AreEqual("space", group.GetProperty("fields").GetProperty("dimension").GetString());
                Assert.IsNotNull(group.GetProperty("fields").GetProperty("name").GetString());
            }
        }

        /// <summary>
        /// The groupBy query must apply the same filter predicates as the items query.
        /// </summary>
        [TestMethod]
        public async Task TestGroupByWithFilter()
        {
            string gqlQuery = @"{
                planets(" + QueryBuilder.FILTER_FIELD_NAME + @": { name: { eq: ""Earth"" } }) {
                    groupBy(fields: [name]) {
                        fields { name }
                        aggregations { count(field: id) }
                    }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            JsonElement groups = result.GetProperty(QueryBuilder.GROUP_BY_FIELD_NAME);

            Assert.AreEqual(1, groups.GetArrayLength(), "Only the Earth group survives the filter");
            Assert.AreEqual("Earth", groups[0].GetProperty("fields").GetProperty("name").GetString());
            Assert.AreEqual(2, groups[0].GetProperty("aggregations").GetProperty("count").GetInt32());
        }

        /// <summary>
        /// A filter matching nothing yields an empty group list rather than an error or null.
        /// </summary>
        [TestMethod]
        public async Task TestGroupByWithNoMatches()
        {
            string gqlQuery = @"{
                planets(" + QueryBuilder.FILTER_FIELD_NAME + @": { name: { eq: ""DoesNotExist"" } }) {
                    groupBy(fields: [name]) {
                        fields { name }
                        aggregations { count(field: id) }
                    }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            Assert.AreEqual(0, result.GetProperty(QueryBuilder.GROUP_BY_FIELD_NAME).GetArrayLength());
        }

        /// <summary>
        /// Selecting only aggregations, with no `fields` sub-selection, is valid - the grouping
        /// columns are still projected so the rows can be split, they are just not returned.
        /// </summary>
        [TestMethod]
        public async Task TestGroupByAggregationsWithoutFieldsSelection()
        {
            string gqlQuery = @"{
                planets {
                    groupBy(fields: [name]) {
                        aggregations { count(field: id) }
                    }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            JsonElement groups = result.GetProperty(QueryBuilder.GROUP_BY_FIELD_NAME);

            Assert.AreEqual(_distinctPlanetNames, groups.GetArrayLength());
            Assert.AreEqual(_seededDocCount, groups.EnumerateArray().Sum(g => g.GetProperty("aggregations").GetProperty("count").GetInt32()));
        }

        /// <summary>
        /// Cosmos NoSQL has no HAVING clause. The argument exists on the shared groupBy schema,
        /// so it must be rejected explicitly rather than silently ignored - a dropped HAVING
        /// would return more groups than the caller asked for.
        /// </summary>
        [TestMethod]
        public async Task TestGroupByHavingIsRejected()
        {
            string gqlQuery = @"{
                planets {
                    groupBy(fields: [name]) {
                        aggregations { count(field: age, having: { gt: 1 }) }
                    }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            SqlTestHelper.TestForErrorInGraphQLResponse(result.ToString(), message: "'having' argument is not supported for Cosmos DB NoSQL");
        }

        /// <summary>
        /// Cosmos NoSQL cannot apply DISTINCT inside an aggregate function.
        /// </summary>
        [TestMethod]
        public async Task TestGroupByDistinctIsRejected()
        {
            string gqlQuery = @"{
                planets {
                    groupBy(fields: [name]) {
                        aggregations { count(field: age, distinct: true) }
                    }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            SqlTestHelper.TestForErrorInGraphQLResponse(result.ToString(), message: "'distinct' argument is not supported for Cosmos DB NoSQL");
        }

        /// <summary>
        /// Cosmos NoSQL cannot combine ORDER BY with GROUP BY.
        /// </summary>
        [TestMethod]
        public async Task TestGroupByWithOrderByIsRejected()
        {
            string gqlQuery = @"{
                planets(orderBy: { name: ASC }) {
                    groupBy(fields: [name]) {
                        fields { name }
                    }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            SqlTestHelper.TestForErrorInGraphQLResponse(result.ToString(), message: "orderBy is not supported together with groupBy");
        }

        /// <summary>
        /// Fields selected under groupBy.fields must be a subset of the groupBy argument,
        /// otherwise the projection would reference a column that was not grouped on.
        /// </summary>
        [TestMethod]
        public async Task TestGroupByFieldSelectionOutsideArgumentIsRejected()
        {
            string gqlQuery = @"{
                planets {
                    groupBy(fields: [name]) {
                        fields { name dimension }
                    }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            SqlTestHelper.TestForErrorInGraphQLResponse(result.ToString(), message: "Groupby fields in selection must match the fields in the groupby argument.");
        }

        /// <summary>
        /// Selecting both items and groupBy in one query is not supported by the shared
        /// pagination plumbing and must fail rather than return one of the two.
        /// </summary>
        [TestMethod]
        public async Task TestGroupByWithItemsIsRejected()
        {
            string gqlQuery = @"{
                planets {
                    items { id }
                    groupBy(fields: [name]) {
                        fields { name }
                    }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            SqlTestHelper.TestForErrorInGraphQLResponse(result.ToString(), message: "Cannot have both groupBy and items in the same query");
        }
    }
}

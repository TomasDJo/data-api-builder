// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.DataApiBuilder.Core.Resolvers;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QueryBuilder = Azure.DataApiBuilder.Service.GraphQLBuilder.Queries.QueryBuilder;

namespace Azure.DataApiBuilder.Service.Tests.CosmosTests
{
    /// <summary>
    /// Integration tests for the Connection-level `count: Int!` field exposed
    /// on Cosmos NoSQL entities. Verifies that the count reflects the total
    /// number of documents matching the filter regardless of pagination, that
    /// the count can be selected with or without items, and that filter
    /// predicates are applied identically to the items query.
    /// </summary>
    [TestClass, TestCategory(TestCategory.COSMOSDBNOSQL)]
    public class QueryCountTests : TestBase
    {
        private const string _graphQLQueryName = "planets";
        private const int _seededDocCount = 10;

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
        /// count alone, no items: returns the total document count for the
        /// container without any filter applied.
        /// </summary>
        [TestMethod]
        public async Task TestCountOnly_NoFilter()
        {
            string gqlQuery = @"{
                planets(first: 10) {
                    count
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            Assert.AreEqual(_seededDocCount, result.GetProperty("count").GetInt32());
        }

        /// <summary>
        /// count + items in the same query: count returns the total matching
        /// regardless of `first`, items returns up to `first` documents.
        /// </summary>
        [TestMethod]
        public async Task TestCountAndItems_PaginationIndependent()
        {
            string gqlQuery = @"{
                planets(first: 3) {
                    count
                    items { id name }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            Assert.AreEqual(_seededDocCount, result.GetProperty("count").GetInt32());
            Assert.AreEqual(3, result.GetProperty("items").GetArrayLength());
        }

        /// <summary>
        /// count with a filter: must use the same WHERE clause as the items
        /// query and return only matching documents. Seed data uses
        /// round-robin planet names from a 9-element array, so for 10 docs we
        /// expect 2 with name "Earth" and 1 of each other name.
        /// </summary>
        [TestMethod]
        public async Task TestCountWithFilter()
        {
            string gqlQuery = @"{
                planets(first: 10, " + QueryBuilder.FILTER_FIELD_NAME + @": { name: { eq: ""Earth"" } }) {
                    count
                    items { name }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            int count = result.GetProperty("count").GetInt32();
            int itemsLength = result.GetProperty("items").GetArrayLength();
            Assert.AreEqual(itemsLength, count, "count should equal the number of items returned when first >= count");
            Assert.IsTrue(count >= 1, "Expected at least one Earth document");
        }

        /// <summary>
        /// count returns 0 when the filter matches nothing.
        /// </summary>
        [TestMethod]
        public async Task TestCountWithNoMatches()
        {
            string gqlQuery = @"{
                planets(first: 10, " + QueryBuilder.FILTER_FIELD_NAME + @": { name: { eq: ""DoesNotExist"" } }) {
                    count
                    items { name }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            Assert.AreEqual(0, result.GetProperty("count").GetInt32());
            Assert.AreEqual(0, result.GetProperty("items").GetArrayLength());
        }

        /// <summary>
        /// count combined with hasNextPage: when more documents exist than the
        /// requested page size, hasNextPage is true and count still reports
        /// the full total.
        /// </summary>
        [TestMethod]
        public async Task TestCountWithPaginationFlags()
        {
            string gqlQuery = @"{
                planets(first: 2) {
                    count
                    hasNextPage
                    items { id }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            Assert.AreEqual(_seededDocCount, result.GetProperty("count").GetInt32());
            Assert.IsTrue(result.GetProperty("hasNextPage").GetBoolean());
            Assert.AreEqual(2, result.GetProperty("items").GetArrayLength());
        }

        /// <summary>
        /// Sanity check: queries that don't select count continue to work and
        /// don't include a count field in the response.
        /// </summary>
        [TestMethod]
        public async Task TestItemsOnly_NoCountSelected()
        {
            string gqlQuery = @"{
                planets(first: 10) {
                    items { id name }
                }
            }";

            JsonElement result = await ExecuteGraphQLRequestAsync(_graphQLQueryName, gqlQuery);
            Assert.AreEqual(_seededDocCount, result.GetProperty("items").GetArrayLength());
            Assert.IsFalse(result.TryGetProperty("count", out _), "count should not appear when not selected");
        }
    }
}

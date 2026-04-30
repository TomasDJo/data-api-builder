// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.DataApiBuilder.Core.Resolvers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Azure.DataApiBuilder.Service.Tests.CosmosTests
{
    /// <summary>
    /// Pure-function tests for <see cref="CosmosQueryBuilder.FormatPropertyAccess"/>.
    /// Cosmos NoSQL property names that match reserved keywords, contain
    /// non-identifier characters, or start with a digit must be rendered with
    /// bracket notation; everything else can use dot notation. The service
    /// returns SC1001 syntax errors otherwise.
    /// </summary>
    [TestClass, TestCategory(TestCategory.COSMOSDBNOSQL)]
    public class CosmosReservedKeywordTests
    {
        [DataTestMethod]
        [DataRow("name", "c.name", DisplayName = "Plain identifier uses dot notation")]
        [DataRow("makeName", "c.makeName", DisplayName = "Camel-case identifier uses dot notation")]
        [DataRow("_ts", "c._ts", DisplayName = "Underscore-prefixed identifier uses dot notation")]
        public void FormatPropertyAccess_PlainIdentifier_UsesDotNotation(string propertyName, string expected)
        {
            string actual = CosmosQueryBuilder.FormatPropertyAccess("c", propertyName);
            Assert.AreEqual(expected, actual);
        }

        [DataTestMethod]
        [DataRow("from", "c[\"from\"]", DisplayName = "Reserved keyword 'from' uses bracket notation")]
        [DataRow("FROM", "c[\"FROM\"]", DisplayName = "Reserved keyword check is case-insensitive")]
        [DataRow("where", "c[\"where\"]", DisplayName = "Reserved keyword 'where' uses bracket notation")]
        [DataRow("order", "c[\"order\"]", DisplayName = "Reserved keyword 'order' uses bracket notation")]
        [DataRow("group", "c[\"group\"]", DisplayName = "Reserved keyword 'group' uses bracket notation")]
        [DataRow("join", "c[\"join\"]", DisplayName = "Reserved keyword 'join' uses bracket notation")]
        [DataRow("value", "c[\"value\"]", DisplayName = "Reserved keyword 'value' uses bracket notation")]
        public void FormatPropertyAccess_ReservedKeyword_UsesBracketNotation(string propertyName, string expected)
        {
            string actual = CosmosQueryBuilder.FormatPropertyAccess("c", propertyName);
            Assert.AreEqual(expected, actual);
        }

        [DataTestMethod]
        [DataRow("to", "c.to", DisplayName = "'to' is not reserved in Cosmos NoSQL")]
        [DataRow("for", "c.for", DisplayName = "'for' is not reserved in Cosmos NoSQL")]
        [DataRow("if", "c.if", DisplayName = "'if' is not reserved in Cosmos NoSQL")]
        public void FormatPropertyAccess_NotReserved_UsesDotNotation(string propertyName, string expected)
        {
            string actual = CosmosQueryBuilder.FormatPropertyAccess("c", propertyName);
            Assert.AreEqual(expected, actual);
        }

        [DataTestMethod]
        [DataRow("first-name", "c[\"first-name\"]", DisplayName = "Hyphenated property uses bracket notation")]
        [DataRow("first.name", "c[\"first.name\"]", DisplayName = "Dotted property uses bracket notation")]
        [DataRow("address line", "c[\"address line\"]", DisplayName = "Spaced property uses bracket notation")]
        [DataRow("123abc", "c[\"123abc\"]", DisplayName = "Digit-prefixed property uses bracket notation")]
        public void FormatPropertyAccess_SpecialCharacters_UsesBracketNotation(string propertyName, string expected)
        {
            string actual = CosmosQueryBuilder.FormatPropertyAccess("c", propertyName);
            Assert.AreEqual(expected, actual);
        }

        [TestMethod]
        public void FormatPropertyAccess_EmbeddedQuote_IsEscaped()
        {
            string actual = CosmosQueryBuilder.FormatPropertyAccess("c", "ev\"il");
            Assert.AreEqual("c[\"ev\\\"il\"]", actual);
        }

        [TestMethod]
        public void FormatPropertyAccess_EmbeddedBackslash_IsEscaped()
        {
            string actual = CosmosQueryBuilder.FormatPropertyAccess("c", "back\\slash");
            Assert.AreEqual("c[\"back\\\\slash\"]", actual);
        }

        [TestMethod]
        public void FormatPropertyAccess_NestedAlias_KeepsAlias()
        {
            string actual = CosmosQueryBuilder.FormatPropertyAccess("table0", "from");
            Assert.AreEqual("table0[\"from\"]", actual);
        }
    }
}

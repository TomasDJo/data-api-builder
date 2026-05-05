// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.DataApiBuilder.Service.GraphQLBuilder.Directives;
using Azure.DataApiBuilder.Service.GraphQLBuilder.GraphQLTypes;
using HotChocolate.Language;
using HotChocolate.Types;
using static Azure.DataApiBuilder.Service.GraphQLBuilder.GraphQLUtils;

namespace Azure.DataApiBuilder.Service.GraphQLBuilder.Queries
{
    public static class InputTypeBuilder
    {
        // Emitted on auto-generated *OrderByInput types whose source object has no
        // scalar fields. HotChocolate refuses to build a schema where any
        // InputObject has zero fields, and order-by is only meaningful for scalars,
        // so we add a no-op placeholder rather than skip generation (which would
        // break consumers that reference the input type by name).
        internal const string SYNTHETIC_ORDER_BY_PLACEHOLDER = "_dabPlaceholder";

        public static void GenerateInputTypesForObjectType(ObjectTypeDefinitionNode node, IDictionary<string, InputObjectTypeDefinitionNode> inputTypes)
        {
            GenerateOrderByInputTypeForObjectType(node, inputTypes);
            GenerateFilterInputTypeForObjectType(node, inputTypes);
        }

        public static void GenerateFilterInputTypeForObjectType(
            ObjectTypeDefinitionNode node,
            IDictionary<string, InputObjectTypeDefinitionNode> inputTypes
        )
        {
            List<InputValueDefinitionNode> inputFields = GenerateFilterInputFieldsForBuiltInFields(node, inputTypes);
            string filterInputName = GenerateObjectInputFilterName(node);
            GenerateFilterInputTypeFromInputFields(inputTypes, inputFields, filterInputName, $"Filter input for {node.Name} GraphQL type");
        }

        internal static void GenerateOrderByInputTypeForObjectType(ObjectTypeDefinitionNode node, IDictionary<string, InputObjectTypeDefinitionNode> inputTypes)
        {
            List<InputValueDefinitionNode> inputFields = GenerateOrderByInputFieldsForBuiltInFields(node);
            string orderByInputName = GenerateObjectInputOrderByName(node);

            // A type whose fields are all object-typed (e.g. a wrapper of nested
            // entities) has no orderable scalars, so the input would be empty and
            // HotChocolate would fail schema build with "InputObject has no fields
            // declared". Emit a no-op placeholder to keep the type non-empty.
            //
            // Trade-off: the placeholder is visible in introspection / the published
            // schema document. Cleaner long-term fix is to skip emitting the empty
            // *OrderByInput AND drop the `orderBy:` argument from any field whose
            // target type has no orderable scalars. That requires coordinating with
            // QueryBuilder.GenerateListQueryFields / QueryArgumentsForField (the
            // current contract assumes every list/relationship field gets an
            // orderBy arg of NamedType orderByInputName). Search for
            // SYNTHETIC_ORDER_BY_PLACEHOLDER when picking this up.
            if (inputFields.Count == 0)
            {
                inputFields.Add(
                    new(
                        location: null,
                        new NameNode(SYNTHETIC_ORDER_BY_PLACEHOLDER),
                        new StringValueNode("Placeholder added because the source type has no orderable scalar fields. Has no effect when supplied."),
                        new NamedTypeNode(OrderByType.EnumName),
                        defaultValue: null,
                        new List<DirectiveNode>())
                    );
            }

            // OrderBy does not include "and" and "or" input types so we add only the orderByInputName here.
            inputTypes.Add(
                orderByInputName,
                new(
                    location: null,
                    new NameNode(orderByInputName),
                    new StringValueNode($"Order by input for {node.Name} GraphQL type"),
                    new List<DirectiveNode>(),
                    inputFields
                    )
                );
        }

        private static List<InputValueDefinitionNode> GenerateOrderByInputFieldsForBuiltInFields(ObjectTypeDefinitionNode node)
        {
            List<InputValueDefinitionNode> inputFields = new();
            foreach (FieldDefinitionNode field in node.Fields)
            {
                if (IsBuiltInType(field.Type))
                {
                    inputFields.Add(
                        new(
                            location: null,
                            field.Name,
                            new StringValueNode($"Order by options for {field.Name}"),
                            new NamedTypeNode(OrderByType.EnumName),
                            defaultValue: null,
                            new List<DirectiveNode>())
                        );
                }
            }

            return inputFields;
        }

        private static void GenerateFilterInputTypeFromInputFields(
            IDictionary<string, InputObjectTypeDefinitionNode> inputTypes,
            List<InputValueDefinitionNode> inputFields,
            string inputTypeName,
            string inputTypeDescription)
        {
            inputFields.Add(
                new(
                    location: null,
                    new("and"),
                    new("Conditions to be treated as AND operations"),
                    new ListTypeNode(new NamedTypeNode(inputTypeName)),
                    defaultValue: null,
                    new List<DirectiveNode>()));

            inputFields.Add(
                new(
                    location: null,
                    new("or"),
                    new("Conditions to be treated as OR operations"),
                    new ListTypeNode(new NamedTypeNode(inputTypeName)),
                    defaultValue: null,
                    new List<DirectiveNode>()));

            inputTypes.Add(
                inputTypeName,
                new(
                    location: null,
                    new NameNode(inputTypeName),
                    new StringValueNode(inputTypeDescription),
                    new List<DirectiveNode>(),
                    inputFields
                )
            );
        }

        private static List<InputValueDefinitionNode> GenerateFilterInputFieldsForBuiltInFields(
            ObjectTypeDefinitionNode objectTypeDefinitionNode,
            IDictionary<string, InputObjectTypeDefinitionNode> inputTypes)
        {
            List<InputValueDefinitionNode> inputFields = new();
            foreach (FieldDefinitionNode field in objectTypeDefinitionNode.Fields)
            {
                string fieldTypeName = field.Type.NamedType().Name.Value;
                if (IsBuiltInType(field.Type))
                {
                    if (!inputTypes.ContainsKey(fieldTypeName))
                    {
                        inputTypes.Add(fieldTypeName, StandardQueryInputs.GetFilterTypeByScalar(fieldTypeName));
                    }

                    InputObjectTypeDefinitionNode inputType = inputTypes[fieldTypeName];

                    inputFields.Add(
                        new(
                            location: null,
                            field.Name,
                            new StringValueNode($"Filter options for {field.Name}"),
                            new NamedTypeNode(inputType.Name.Value),
                            defaultValue: null,
                            new List<DirectiveNode>())
                        );
                }
                else
                {
                    string targetEntityName = RelationshipDirectiveType.Target(field);

                    DirectiveNode? relationshipDirective =
                        RelationshipDirectiveType.GetDirective(field);
                    List<DirectiveNode> directives = new();
                    if (relationshipDirective is not null)
                    {
                        directives.Add(relationshipDirective);
                    }

                    inputFields.Add(
                        new(
                            location: null,
                            field.Name,
                            new StringValueNode($"Filter options for {field.Name}"),
                            new NamedTypeNode(GenerateObjectInputFilterName(targetEntityName)),
                            defaultValue: null,
                            directives: directives));
                }
            }

            return inputFields;
        }

        internal static string GenerateObjectInputOrderByName(string name)
        {
            return $"{name}OrderByInput";
        }

        private static string GenerateObjectInputOrderByName(ObjectTypeDefinitionNode node)
        {
            return GenerateObjectInputOrderByName(node.Name.Value);
        }

        private static string GenerateObjectInputFilterName(ObjectTypeDefinitionNode node)
        {
            return GenerateObjectInputFilterName(node.Name.Value);
        }

        public static string GenerateObjectInputFilterName(string name)
        {
            return $"{name}FilterInput";
        }
    }
}

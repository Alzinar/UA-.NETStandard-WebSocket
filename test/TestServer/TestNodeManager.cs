using System.Collections.Generic;
using Opc.Ua;
using Opc.Ua.Server;

namespace TestServer
{
    /// <summary>
    /// Namespace URI used for the single test variable exposed by this node manager.
    /// </summary>
    internal static class TestNamespace
    {
        public const string Uri = "http://test.local/UaWebSocket";
    }

    /// <summary>
    /// Exposes a single writable Int32 variable under the Objects folder for the WebSocket test.
    /// </summary>
    internal sealed class TestNodeManager : CustomNodeManager2
    {
        public TestNodeManager(IServerInternal server, ApplicationConfiguration configuration)
            : base(server, configuration, TestNamespace.Uri)
        {
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference> references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }

                var folder = new FolderState(null)
                {
                    NodeId = new NodeId("WebSocketTest", NamespaceIndex),
                    BrowseName = new QualifiedName("WebSocketTest", NamespaceIndex),
                    DisplayName = "WebSocketTest",
                    TypeDefinitionId = ObjectTypeIds.FolderType,
                    ReferenceTypeId = ReferenceTypeIds.Organizes
                };
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));

                var int32Value = new BaseDataVariableState<int>(folder)
                {
                    NodeId = new NodeId("Int32Value", NamespaceIndex),
                    BrowseName = new QualifiedName("Int32Value", NamespaceIndex),
                    DisplayName = "Int32Value",
                    DataType = DataTypeIds.Int32,
                    ValueRank = ValueRanks.Scalar,
                    AccessLevel = AccessLevels.CurrentReadOrWrite,
                    UserAccessLevel = AccessLevels.CurrentReadOrWrite,
                    Value = 0
                };
                folder.AddChild(int32Value);

                AddPredefinedNode(SystemContext, folder);
            }
        }
    }

    /// <summary>
    /// Creates <see cref="TestNodeManager"/> instances for a <see cref="StandardServer"/>.
    /// </summary>
    internal sealed class TestNodeManagerFactory : INodeManagerFactory
    {
        public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
        {
            return new TestNodeManager(server, configuration);
        }

        public StringCollection NamespacesUris => new StringCollection { TestNamespace.Uri };
    }
}

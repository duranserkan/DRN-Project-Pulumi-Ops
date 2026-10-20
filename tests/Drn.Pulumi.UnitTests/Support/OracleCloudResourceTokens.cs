namespace Drn.Pulumi.UnitTests.Support;

// Provider serialization contracts. The pinned generated SDKs do not expose public token constants.
internal static class OracleCloudResourceTokens
{
    internal const string Bucket = "oci:ObjectStorage/bucket:Bucket";
    internal const string Volume = "oci:Core/volume:Volume";
    internal const string VolumeGroup = "oci:Core/volumeGroup:VolumeGroup";
    internal const string BackupPolicy = "oci:Core/volumeBackupPolicy:VolumeBackupPolicy";
    internal const string BackupPolicyAssignment = "oci:Core/volumeBackupPolicyAssignment:VolumeBackupPolicyAssignment";
    internal const string Cluster = "oci:ContainerEngine/cluster:Cluster";
    internal const string NodePool = "oci:ContainerEngine/nodePool:NodePool";
    internal const string SecurityList = "oci:Core/securityList:SecurityList";
    internal const string ServiceGateway = "oci:Core/serviceGateway:ServiceGateway";
    internal const string Provider = "pulumi:providers:oci";
    internal const string GetServices = "oci:Core/getServices:getServices";
}

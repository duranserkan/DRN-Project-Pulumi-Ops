using Pulumi;
using Pulumi.Oci.Core;
using Pulumi.Oci.Core.Inputs;
using Oci = Pulumi.Oci;

namespace Drn.Pulumi;

internal static class Durable
{
    internal static IDictionary<string, object?> Create(InfrastructureSettings settings, Oci.Provider provider)
    {
        Dictionary<string, Output<string>> activeIds = [];
        Output<string>? activeGroup = null;
        var policy = new VolumeBackupPolicy("staging-daily-fallback", new VolumeBackupPolicyArgs
        {
            CompartmentId = settings.DurableCompartmentId,
            DisplayName = "drn-staging-daily-crash-consistent",
            Schedules = { new VolumeBackupPolicyScheduleArgs
            {
                Period = "ONE_DAY", BackupType = "INCREMENTAL", RetentionSeconds = 604800,
                OffsetType = "STRUCTURED", HourOfDay = 3, TimeZone = "UTC"
            } }
        }, new CustomResourceOptions { Provider = provider });
        foreach (var generation in settings.StorageGenerations)
        {
            var ids = new Dictionary<string, Output<string>>();
            var prefix = generation.Id == "initial" ? "staging" : $"staging-{generation.Id}";
            foreach (var claim in StorageClaims.All)
            {
                var profile = generation.Volume(claim.Key);
                var volumeArgs = new VolumeArgs
                {
                    CompartmentId = settings.DurableCompartmentId,
                    AvailabilityDomain = settings.AvailabilityDomain,
                    DisplayName = $"drn-{prefix}-{claim.Key}",
                    SizeInGbs = profile.SizeInGbs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    VpusPerGb = profile.VpusPerGb.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    IsAutoTuneEnabled = false,
                    FreeformTags =
                    {
                        ["project"] = "DRN-Project", ["environment"] = "staging",
                        ["generation"] = generation.Id, ["claim"] = $"{claim.Namespace}/{claim.ClaimName}"
                    }
                };
                if (generation.BackupIds is not null)
                    volumeArgs.SourceDetails = new VolumeSourceDetailsArgs
                    {
                        Type = "volumeBackup", Id = generation.BackupIds[claim.Key]
                    };
                var volume = new Volume($"{prefix}-{claim.Key}", volumeArgs,
                    new CustomResourceOptions { Provider = provider, Protect = true });
                ids.Add(claim.Key, volume.Id);
            }
            var groupVolumeIds = new InputList<string>();
            foreach (var id in ids.Values) groupVolumeIds.Add(id);
            var group = new VolumeGroup($"{prefix}-data", new VolumeGroupArgs
            {
                AvailabilityDomain = settings.AvailabilityDomain,
                CompartmentId = settings.DurableCompartmentId,
                DisplayName = $"drn-{prefix}-data",
                SourceDetails = new VolumeGroupSourceDetailsArgs { Type = "volumeIds", VolumeIds = groupVolumeIds }
            }, new CustomResourceOptions { Provider = provider, Protect = true });
            if (generation.Id == settings.ActiveStorageGeneration)
            {
                activeIds = ids;
                activeGroup = group.Id;
            }
        }
        // Only the active generation receives automatic crash-consistent fallback snapshots.
        _ = new VolumeBackupPolicyAssignment("staging-active-snapshot-policy", new VolumeBackupPolicyAssignmentArgs
        {
            AssetId = activeGroup!, PolicyId = policy.Id
        }, new CustomResourceOptions { Provider = provider });
        return new Dictionary<string, object?>
        {
            ["region"] = settings.Region,
            ["availabilityDomain"] = settings.AvailabilityDomain,
            ["volumeIds"] = activeIds,
            ["volumeGroupId"] = activeGroup!,
            ["storageGeneration"] = settings.ActiveStorageGeneration,
            ["volumeSizesInGbs"] = StorageClaims.All.ToDictionary(claim => claim.Key,
                claim => settings.ActiveStorage.Volume(claim.Key).SizeInGbs),
            ["claims"] = StorageClaims.All.Select(claim => new Dictionary<string, object?>
            {
                ["key"] = claim.Key, ["namespace"] = claim.Namespace, ["claimName"] = claim.ClaimName
            }).ToArray()
        };
    }
}

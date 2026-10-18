# DRN Project Pulumi Ops

[![DRN-Project](https://img.shields.io/badge/DRN--Project-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://github.com/duranserkan/DRN-Project)
[![DRN-Project-Argo-CD-GitOps](https://img.shields.io/badge/DRN--Project--Argo--CD--GitOps-EF7B4D?style=for-the-badge&logo=argo&logoColor=white)](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops)

[![Docker Hub](https://img.shields.io/badge/images-blue?logo=docker&label=dockerhub
)](https://hub.docker.com/u/duranserkan)
[![Nuget](https://img.shields.io/badge/packages-004880?logo=nuget&label=nuget
)](https://www.nuget.org/profiles/duranserkan)

[![Pulumi Documentation](https://img.shields.io/badge/Doc-Pulumi_Documentation-F7BF2A)](https://www.pulumi.com/docs/)
[![Pulumi Oracle Cloud Infrastructure Documentation](https://img.shields.io/badge/Doc-Pulumi_OCI_Documentation-F7BF2A)](https://www.pulumi.com/registry/packages/oci/)
[![Oracle Cloud Infrastructure Documentation](https://img.shields.io/badge/Doc-OCI_Documentation-F80000)](https://docs.oracle.com/en-us/iaas/Content/home.htm)

[![Awesome Pulumi](https://img.shields.io/badge/Doc-Awesome_Pulumi-F7BF2A)](https://github.com/pulumiverse/awesome-pulumi)
[![Awesome Oracle Cloud Free Tier](https://img.shields.io/badge/Doc-Awesome_Oracle_Cloud_Free_Tier-F80000)](https://github.com/neitsab/awesome-oracle-cloud-free-tier)

Oracle Cloud infrastructure for DRN-Project using Pulumi, with Argo CD GitOps, persistent storage, and automated deployment, teardown, and recovery.

**Status:** Initial repository setup. Provisioning automation is not implemented yet.

Planned responsibilities:

- Provision Oracle Kubernetes Engine clusters, networking and workers with Pulumi and C#.
- Bootstrap Argo CD from a pinned GitOps release.
- Automate deployment and teardown through a single command for each operation.
- Preserve application data and recovery material when recreating a cluster.

DRN-Project owns application source and image publishing. Argo CD GitOps owns platform and workload manifests. This repository owns the Oracle Cloud infrastructure lifecycle.

Keep credentials out of Git. Use generic placeholders in documentation and store Pulumi state and recovery material outside disposable clusters.

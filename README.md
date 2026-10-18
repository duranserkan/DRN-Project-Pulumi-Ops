# DRN Project Pulumi Ops

[![DRN-Project](https://img.shields.io/badge/DRN--Project-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://github.com/duranserkan/DRN-Project)
[![DRN-Project-Argo-CD-GitOps](https://img.shields.io/badge/DRN--Project--Argo--CD--GitOps-EF7B4D?style=for-the-badge&logo=argo&logoColor=white)](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops)

Oracle Cloud infrastructure for DRN-Project using Pulumi, with Argo CD GitOps, persistent storage, and automated deployment, teardown, and recovery.

**Status:** Initial repository setup. Provisioning automation is not implemented yet.

Planned responsibilities:

- Provision Oracle Kubernetes Engine clusters, networking and workers with Pulumi and C#.
- Bootstrap Argo CD from a pinned GitOps release.
- Automate deployment and teardown through a single command for each operation.
- Preserve application data and recovery material when recreating a cluster.

DRN-Project owns application source and image publishing. Argo CD GitOps owns platform and workload manifests. This repository owns the Oracle Cloud infrastructure lifecycle.

Keep credentials out of Git. Use generic placeholders in documentation and store Pulumi state and recovery material outside disposable clusters.

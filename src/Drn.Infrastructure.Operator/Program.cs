using Drn.Infrastructure.Operator;

if (args.Length == 0 || args is ["--help"] or ["help"])
{
    Console.WriteLine("""
        DRN infrastructure operations (run from the repository root)

        dotnet run --project src/Drn.Infrastructure.Operator -- <command> .local/staging.json [--yes]

        validate     Check private configuration without contacting OCI
        keygen       Create the private recovery key without replacing an existing key
        setup        Select four stacks and provision the protected recovery bucket
        initialize   Save and verify credentials for new, empty infrastructure
        preview      Preview durable and cluster resources
        durable      Provision only durable storage before initial bootstrap
        cluster      Provision only the cluster before initial bootstrap
        up           Provision, verify policy, activate GitOps and take the first backup
        bootstrap    Install only bootstrap resources; leave workload sync disabled
        status       Show lifecycle state, backup IDs and shared lock ownership
        check        Verify backup freshness, identities and applicable live readiness
        kubeconfig   Write a protected, dedicated kubeconfig
        backup       Quiesce, export databases, snapshot detached disks, then resume
        pause        Take a complete checkpoint and leave workers stopped
        resume       Recover the recorded generation and activate workloads
        down         Checkpoint and remove disposable infrastructure; keep durable data
        restore      Recover a selected checkpoint to new protected disks
        release      Select a reviewed compatible GitOps release while offline or recovering startup
        unlock       Release a failed operation after its previous process is stopped

        down requires --confirm staging.
        release requires --confirm staging; pause or down first for a normal release change.
        restore requires --confirm staging --backup <checkpoint-id> --allow-additional-storage.
        unlock requires --confirm staging --owner <operation-id> --previous-process-stopped.
        Pulumi prompts unless --yes is supplied. Complete backups cause staging downtime.
        Recovery needs the independent key; failed operations retain their shared lock.
        durable, cluster and bootstrap require initialized state. Bootstrap needs both stacks.
        After bootstrap, use up/resume for activation and all later lifecycle changes.
        """);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var command = OperatorCommand.Parse(args);
    var configuration = OperatorConfiguration.Load(command.ConfigurationPath);
    await new InfrastructureOperator(Directory.GetCurrentDirectory(), configuration, new ProcessRunner())
        .ExecuteAsync(command, cancellation.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operation cancelled. Inspect stack status before retrying.");
    return 130;
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
catch (Exception)
{
    // Tool responses and configuration can contain private deployment data.
    Console.Error.WriteLine("Operation failed. Check configuration, tool access and stack status before retrying.");
    return 1;
}

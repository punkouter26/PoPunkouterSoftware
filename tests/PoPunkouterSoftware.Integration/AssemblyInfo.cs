// WebApplicationFactory boots the real Program entry point; concurrent boots race in
// HostFactoryResolver ("The entry point exited without ever building an IHost"), which
// the old per-project test split avoided only via process isolation. The merged suite
// runs collections sequentially instead — wall-clock cost is small.
[assembly: CollectionBehavior(DisableTestParallelization = true)]


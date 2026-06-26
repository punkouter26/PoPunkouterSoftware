using PoPunkouterSoftware.Shared.Azure;

namespace PoPunkouterSoftware.UnitTests;

public class AppServicePlanInventoryTests
{
    [Fact]
    public void BuildPoSharedPlanInventory_FiltersToPoSharedServerFarms_AndCountsApps()
    {
        var resources = new List<ResourceDetail>
        {
            new() { Name = "asp-shared-linux", ResourceGroup = "PoShared", Type = "Microsoft.Web/serverFarms", Sku = "B2", Location = "East US" },
            new() { Name = "asp-other", ResourceGroup = "OtherRg", Type = "Microsoft.Web/serverFarms", Sku = "S1", Location = "West US" },
            new() { Name = "asp-shared-2", ResourceGroup = "PoShared", Type = "Microsoft.Web/serverFarms", Sku = "P1v3", Location = "East US" },
        };

        var services = new List<WebService>
        {
            new() { Name = "AppOne", ResourceGroup = "PoShared", AppServicePlan = "asp-shared-linux" },
            new() { Name = "AppTwo", ResourceGroup = "PoShared", AppServicePlan = "asp-shared-linux" },
            new() { Name = "AppThree", ResourceGroup = "PoShared", AppServicePlan = "asp-shared-2" },
        };

        var inventory = AppServicePlanInventory.BuildPoSharedPlanInventory(resources, services);

        inventory.Should().HaveCount(2);
        inventory.Should().ContainSingle(x => x.Name == "asp-shared-linux" && x.AppCount == 2 && x.Sku == "B2");
        inventory.Should().ContainSingle(x => x.Name == "asp-shared-2" && x.AppCount == 1 && x.Sku == "P1v3");
    }
}

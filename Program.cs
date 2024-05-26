using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

// CosmosClient client = new(
//     "https://192.168.64.8:8081/",
//     "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
//     new CosmosClientOptions
//     {
//         HttpClientFactory = () => new HttpClient(
//             new HttpClientHandler
//             {
//                 ServerCertificateCustomValidationCallback =
//                     HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
//             }),
//         ConnectionMode = ConnectionMode.Gateway
//     });

var endpoint = new Uri("https://rojitest.documents.azure.com:443/");

// Use DefaultAzureCredential: respects env vars, Managed Identity, Azure CLI login, VS Code Azure sign-in, etc.
TokenCredential credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    // ExcludeInteractiveBrowserCredential = false // enable if needing interactive browser fallback
});

CosmosClient client = new(
    accountEndpoint: endpoint.ToString(),
    tokenCredential: credential,
    clientOptions: new CosmosClientOptions
    {
        ConnectionMode = ConnectionMode.Gateway
    });

Database database = client.GetDatabase("test");
Container container = database.GetContainer("test");

// Just get the string SQL translation for a LINQ query:
// string[] ids = ["Foo", "Bar"];
// Console.WriteLine(container.GetItemLinqQueryable<Customer>()
//     .Where(c => new[] { "Foo", "Bar" }.Contains(c.CustomerID))
//     .ToString());

// Execute a LINQ query and get the results:
// var customers = container.GetItemLinqQueryable<Customer>(allowSynchronousQueryExecution: true)
//     .Where(c => c.CustomerID == "ALFKI")
//     .ToList();

// Low-level SQL execution, get raw JSON out:
// var feedIterator =
//     container.GetItemQueryStreamIterator("""
//                                          SELECT INDEX_OF(c["Region"], "") AS c
//                                          FROM root c
//                                          WHERE c["Discriminator"] = "Customer"
//                                          """);
// while (feedIterator.HasMoreResults)
// {
//     var response = await feedIterator.ReadNextAsync();
//     var reader = new StreamReader(response.Content);
//     Console.WriteLine(reader.ReadToEnd());
// }

// LINQ to FeedIterator execution:
// using FeedIterator<Customer> setIterator = container.GetItemLinqQueryable<Customer>()
//     .Where(c => c.CustomerID == "ALFKI")
//     .ToFeedIterator();
// var response = await setIterator.ReadNextAsync();
// Console.WriteLine(response.Count);

public class Customer
{
    public string CustomerID { get; set; } = null!;
    public string? Region { get; set; } = null!;
    public int[] Ints { get; set; } = null!;
}


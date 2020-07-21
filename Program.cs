using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

// Program to update GitHub issue types based on labels using GraphQL API
// Processes all issues in the repository that don't have an issue type set
const string githubToken = "..."; // Replace with your GitHub personal access token
const string owner = "dotnet"; // Replace with repository owner
const string repo = "efcore"; // Replace with repository name
const int maxIssueLimit = 0; // Maximum number of issues to process (set to null or 0 for no limit)

using var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitHubIssueTypeUpdater", "1.0"));

// First, fetch all available issue types from the repository
var issueTypes = await GetIssueTypesAsync(httpClient, owner, repo);
if (issueTypes.Count == 0)
{
    Console.WriteLine("No issue types found in repository.");
    return;
}

Console.WriteLine($"Available issue types: {string.Join(", ", issueTypes.Keys)}");
Console.WriteLine();

// Process issues page by page
Console.WriteLine($"Processing issues without issue types (limit: {(maxIssueLimit > 0 ? maxIssueLimit.ToString() : "none")})...");
int processedCount = 0;
int updatedCount = 0;

await ProcessIssuesWithoutIssueTypesAsync(httpClient, owner, repo, issueTypes, maxIssueLimit,
    (processed, updated) =>
    {
        processedCount = processed;
        updatedCount = updated;
    });

Console.WriteLine($"Processing complete. Processed {processedCount} issues, updated {updatedCount} issues.");

static async Task ProcessIssuesWithoutIssueTypesAsync(
    HttpClient httpClient,
    string owner,
    string repo,
    Dictionary<string, string> issueTypes,
    int maxLimit,
    Action<int, int> progressCallback)
{
    string? cursor = null;
    int processedCount = 0;
    int updatedCount = 0;

    do
    {
        var query = """
            query GetIssuesWithoutTypes($owner: String!, $repo: String!, $after: String) {
                repository(owner: $owner, name: $repo) {
                    issues(first: 100, after: $after, labels: ["type-cleanup"]) {
                        pageInfo {
                            hasNextPage
                            endCursor
                        }
                        nodes {
                            id
                            number
                            title
                            issueType {
                                id
                                name
                            }
                            labels(first: 20) {
                                nodes {
                                    id
                                    name
                                }
                            }
                        }
                    }
                }
            }
            """;

        var variables = new { owner, repo, after = cursor };
        var request = new { query, variables };

        Console.WriteLine($"Fetching next page of issues (processed so far: {processedCount})...");
        var response = await ExecuteGraphQLAsync(httpClient, request);
        var issuesData = response.GetProperty("data").GetProperty("repository").GetProperty("issues");

        var issuesInThisPage = new List<IssueInfo>();

        foreach (var issueNode in issuesData.GetProperty("nodes").EnumerateArray())
        {
            // Only include issues without issue types
            if (!issueNode.TryGetProperty("issueType", out var issueTypeElement) ||
                issueTypeElement.ValueKind == JsonValueKind.Null)
            {
                var issue = new IssueInfo
                {
                    Id = issueNode.GetProperty("id").GetString()!,
                    Number = issueNode.GetProperty("number").GetInt32(),
                    Title = issueNode.GetProperty("title").GetString()!
                };

                // Get labels
                if (issueNode.TryGetProperty("labels", out var labelsElement))
                {
                    foreach (var label in labelsElement.GetProperty("nodes").EnumerateArray())
                    {
                        var labelName = label.GetProperty("name").GetString()!;
                        var labelId = label.GetProperty("id").GetString()!;
                        issue.Labels.Add(labelName);
                        issue.LabelIds[labelName] = labelId;
                    }
                }

                issuesInThisPage.Add(issue);
            }
        }

        // Process the issues from this page immediately
        foreach (var issue in issuesInThisPage)
        {
            // Check if we've reached the limit
            if (maxLimit > 0 && processedCount >= maxLimit)
            {
                Console.WriteLine($"Reached processing limit of {maxLimit} issues. Stopping.");
                progressCallback(processedCount, updatedCount);
                return;
            }

            processedCount++;
            Console.WriteLine($"Processing issue #{issue.Number}: {issue.Title}");
            Console.WriteLine($"Labels: {string.Join(", ", issue.Labels)}");

            var result = await ProcessIssueAsync(httpClient, issue, issueTypes);
            if (result)
            {
                updatedCount++;
                // await Task.Delay(100);
            }

            Console.WriteLine();
        }

        // Update progress
        progressCallback(processedCount, updatedCount);

        // Check if there are more pages
        var pageInfo = issuesData.GetProperty("pageInfo");
        if (pageInfo.GetProperty("hasNextPage").GetBoolean())
        {
            cursor = pageInfo.GetProperty("endCursor").GetString();
        }
        else
        {
            cursor = null;
        }

    } while (cursor != null);

    if (processedCount == 0)
    {
        Console.WriteLine("No issues found without issue types.");
    }
}

static async Task<bool> ProcessIssueAsync(HttpClient httpClient, IssueInfo issue, Dictionary<string, string> issueTypes)
{
    // Determine the desired issue type and label to remove based on labels
    string? desiredIssueType = null;
    string? labelToRemove = null;

    if (issue.Labels.Contains("type-cleanup"))
    {
        desiredIssueType = "Task";
        labelToRemove = "type-cleanup";
    }

    if (desiredIssueType == null || labelToRemove == null)
    {
        Console.WriteLine("  No relevant labels found (cleanup). Skipping.");
        return false;
    }

    // Get the issue type ID
    if (!issueTypes.TryGetValue(desiredIssueType, out var issueTypeId))
    {
        Console.WriteLine($"  {desiredIssueType} issue type not found in repository. Skipping.");
        return false;
    }

    // Get the label ID for the label to remove
    if (!issue.LabelIds.TryGetValue(labelToRemove, out var labelIdToRemove))
    {
        Console.WriteLine($"  Label ID for '{labelToRemove}' not found. Skipping.");
        return false;
    }

    // Update the issue type and remove the label
    try
    {
        await UpdateIssueTypeAndRemoveLabelAsync(httpClient, issue.Id, issueTypeId, desiredIssueType, labelIdToRemove, labelToRemove);
        Console.WriteLine($"  ✓ Updated to {desiredIssueType} and removed '{labelToRemove}' label");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗ Failed to update: {ex.Message}");
        return false;
    }
}

static async Task<Dictionary<string, string>> GetIssueTypesAsync(HttpClient httpClient, string owner, string repo)
{
    var query = """
        query GetIssueTypes($owner: String!, $repo: String!) {
            repository(owner: $owner, name: $repo) {
                issueTypes(first: 10) {
                    nodes {
                        id
                        name
                    }
                }
            }
        }
        """;

    var variables = new { owner, repo };
    var request = new { query, variables };

    Console.WriteLine("Fetching available issue types...");
    var response = await ExecuteGraphQLAsync(httpClient, request);

    var issueTypesNodes = response.GetProperty("data").GetProperty("repository").GetProperty("issueTypes").GetProperty("nodes");

    var issueTypes = new Dictionary<string, string>();
    foreach (var issueType in issueTypesNodes.EnumerateArray())
    {
        var name = issueType.GetProperty("name").GetString()!;
        var id = issueType.GetProperty("id").GetString()!;
        issueTypes[name] = id;
        Console.WriteLine($"Found issue type: {name} (ID: {id})");
    }

    return issueTypes;
}

static async Task UpdateIssueTypeAndRemoveLabelAsync(HttpClient httpClient, string issueId, string issueTypeId, string issueTypeName, string labelId, string labelName)
{
    // First, update the issue type
    var updateIssueMutation = """
        mutation UpdateIssueType($issueId: ID!, $issueTypeId: ID!) {
            updateIssue(input: { id: $issueId, issueTypeId: $issueTypeId }) {
                issue {
                    id
                    title
                    issueType {
                        id
                        name
                    }
                }
            }
        }
        """;

    var updateIssueVariables = new { issueId, issueTypeId };
    var updateIssueRequest = new { query = updateIssueMutation, variables = updateIssueVariables };

    Console.WriteLine($"Updating issue type to {issueTypeName}...");
    var updateResponse = await ExecuteGraphQLAsync(httpClient, updateIssueRequest);

    var updatedIssue = updateResponse.GetProperty("data").GetProperty("updateIssue").GetProperty("issue");
    var updatedIssueType = updatedIssue.GetProperty("issueType").GetProperty("name").GetString();
    Console.WriteLine($"Successfully updated issue type to: {updatedIssueType}");

    // Then, remove the label
    var removeLabelMutation = """
        mutation RemoveLabel($issueId: ID!, $labelId: ID!) {
            removeLabelsFromLabelable(input: { labelableId: $issueId, labelIds: [$labelId] }) {
                labelable {
                    ... on Issue {
                        id
                        labels(first: 20) {
                            nodes {
                                name
                            }
                        }
                    }
                }
            }
        }
        """;

    var removeLabelVariables = new { issueId, labelId };
    var removeLabelRequest = new { query = removeLabelMutation, variables = removeLabelVariables };

    Console.WriteLine($"Removing '{labelName}' label...");
    await ExecuteGraphQLAsync(httpClient, removeLabelRequest);
    Console.WriteLine($"Successfully removed label: {labelName}");
}

static async Task<JsonElement> ExecuteGraphQLAsync(HttpClient httpClient, object request)
{
    var json = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    var response = await httpClient.PostAsync("https://api.github.com/graphql", content);
    var responseContent = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"HTTP error: {response.StatusCode}");
        Console.WriteLine(responseContent);
        throw new HttpRequestException($"GraphQL request failed: {response.StatusCode}");
    }

    using var doc = JsonDocument.Parse(responseContent);

    // Check for GraphQL errors
    if (doc.RootElement.TryGetProperty("errors", out var errorsElement))
    {
        Console.WriteLine("GraphQL errors occurred:");
        foreach (var error in errorsElement.EnumerateArray())
        {
            Console.WriteLine($"  - {error.GetProperty("message").GetString()}");
        }
        throw new InvalidOperationException("GraphQL query contained errors");
    }

    return doc.RootElement.Clone();
}

// Helper class to represent an issue
public class IssueInfo
{
    public string Id { get; set; } = "";
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public List<string> Labels { get; set; } = new();
    public Dictionary<string, string> LabelIds { get; set; } = new();
}

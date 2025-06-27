using Octokit;
using GraphQL;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.Newtonsoft;
using Newtonsoft.Json.Linq;

/*
 * Unified GitHub Issue Type Updater
 * 
 * This application uses unified GraphQL queries for both test mode (single issue) 
 * and production mode (batch processing) to ensure consistency and prevent
 * discrepancies between the two modes.
 * 
 * Key unification features:
 * - Both modes use identical GraphQL query structure (same fields, same data parsing)
 * - Both modes apply the same filtering logic for relevant labels
 * - ParseIssueFromNode() provides common data parsing for consistency
 * - Same error handling and logging approach
 */

// Configuration - UPDATE THESE VALUES
const string GITHUB_TOKEN = "...";
const string REPO_OWNER = "npgsql";
const string REPO_NAME = "efcore.pg";

var github = new GitHubClient(new ProductHeaderValue("issue-type-updater"))
{
    Credentials = new Credentials(GITHUB_TOKEN)
};

var graphQLClient = new GraphQLHttpClient("https://api.github.com/graphql", new NewtonsoftJsonSerializer());
graphQLClient.HttpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {GITHUB_TOKEN}");

// Global variables for issue type node IDs
string? bugIssueTypeId = null;
string? featureIssueTypeId = null;

// Fetch issue type IDs at startup
await FetchIssueTypeIds();

Console.WriteLine("Production mode: Processing all issues without type");
await ProcessAllIssues();

async Task FetchIssueTypeIds()
{
    Console.WriteLine("Fetching issue type node IDs...");
    
    var query = new GraphQLRequest
    {
        Query = """
        query FetchIssueTypes($owner: String!, $name: String!) {
            repository(owner: $owner, name: $name) {
                issueTypes(first: 10) {
                    nodes {
                        id
                        name
                    }
                }
            }
        }
        """,
        Variables = new
        {
            owner = REPO_OWNER,
            name = REPO_NAME
        }
    };

    var response = await graphQLClient.SendQueryAsync<JObject>(query);
    
    if (response?.Data != null)
    {
        var issueTypes = response.Data["repository"]?["issueTypes"]?["nodes"];
        if (issueTypes != null)
        {
            foreach (var issueType in issueTypes)
            {
                var id = issueType["id"]?.ToString();
                var name = issueType["name"]?.ToString();
                
                if (name == "Bug")
                {
                    bugIssueTypeId = id;
                    Console.WriteLine($"Found BUG issue type ID: {id}");
                }
                else if (name == "Feature")
                {
                    featureIssueTypeId = id;
                    Console.WriteLine($"Found FEATURE issue type ID: {id}");
                }
            }
        }
    }
    
    if (bugIssueTypeId == null || featureIssueTypeId == null)
    {
        throw new Exception($"Could not find required issue type IDs. BUG: {bugIssueTypeId}, FEATURE: {featureIssueTypeId}");
    }
}

async Task ProcessAllIssues()
{
    Console.WriteLine("Processing closed issues with full details (page by page)...");
    
    string? cursor = null;
    bool hasNextPage = true;
    int pageNumber = 1;
    int totalProcessed = 0;

    while (hasNextPage)
    {
        Console.WriteLine($"Fetching page {pageNumber}...");
        
        var (issues, nextCursor, hasNext) = await FetchIssuesPage(cursor);
        
        Console.WriteLine($"Page {pageNumber}: Found {issues.Count} issues without type to process");
        
        // Process issues from this page
        foreach (var issueData in issues)
        {
            await ProcessIssueData(issueData);
            totalProcessed++;
            await Task.Delay(1000); // Rate limiting
        }
        
        // Update pagination state
        cursor = nextCursor;
        hasNextPage = hasNext;
        pageNumber++;
        
        Console.WriteLine($"Completed page {pageNumber - 1}. Total processed so far: {totalProcessed}");
    }
    
    Console.WriteLine($"All pages processed. Total issues processed: {totalProcessed}");
}

// Fetch a single page of issues
async Task<(List<IssueData> issues, string? nextCursor, bool hasNextPage)> FetchIssuesPage(string? cursor = null)
{
    var query = new GraphQLRequest
    {
        // issues(first: $first, after: $after, filterBy: { type: null, states: CLOSED, milestoneNumber: "75" }) {
        Query = $$"""
query($owner: String!, $name: String!, $first: Int!, $after: String) {
    repository(owner: $owner, name: $name) {
        issues(first: $first, after: $after, filterBy: { type: null, states: CLOSED }, orderBy: { field: CREATED_AT, direction: DESC }) {
            pageInfo {
                hasNextPage
                endCursor
            }
            nodes {
                id
                number
                state
                timelineItems(last: 100, itemTypes: [LABELED_EVENT]) {
                    nodes {
                        ... on LabeledEvent {
                            createdAt
                            label {
                                name
                            }
                        }
                    }
                }
            }
        }
    }
}
""",
        Variables = new Dictionary<string, object>
        {
            ["owner"] = REPO_OWNER,
            ["name"] = REPO_NAME,
            ["first"] = 50, // Page size
            ["after"] = cursor ?? (object)DBNull.Value
        }
    };

    var response = await graphQLClient.SendQueryAsync<JObject>(query);
    var issues = new List<IssueData>();
    
    if (response?.Data != null)
    {
        var issuesData = response.Data["repository"]?["issues"];
        var pageInfo = issuesData?["pageInfo"];
        var nodes = issuesData?["nodes"];

        var hasNext = pageInfo?["hasNextPage"]?.Value<bool>() ?? false;
        var nextCursor = pageInfo?["endCursor"]?.ToString();

        if (nodes != null)
        {
            foreach (var node in nodes)
            {
                var issueData = ParseIssueFromNode(node);
                if (issueData != null)
                {
                    // Apply filtering logic - only include issues with bug/enhancement labels
                    bool shouldInclude = issueData.LabelEvents.Any(le => 
                        le.LabelName.ToLowerInvariant() == "bug" || 
                        le.LabelName.ToLowerInvariant() == "enhancement");
                        
                    if (shouldInclude)
                    {
                        issues.Add(issueData);
                    }
                }
            }
        }
        
        return (issues, nextCursor, hasNext);
    }
    
    return (issues, null, false);
}

// Unified function to parse issue data from GraphQL response node
IssueData? ParseIssueFromNode(JToken? issueNode)
{
    if (issueNode == null) return null;
    
    var issueNumber = issueNode["number"]?.Value<int>() ?? 0;
    var issueId = issueNode["id"]?.ToString();
    var state = issueNode["state"]?.ToString();
    
    if (issueNumber == 0 || string.IsNullOrEmpty(issueId)) return null;

    // Parse label events using the same logic for both single and batch queries
    var labelEvents = new List<LabelEvent>();
    var timelineItems = issueNode["timelineItems"]?["nodes"];
    if (timelineItems != null)
    {
        foreach (var item in timelineItems)
        {
            var labelName = item["label"]?["name"]?.ToString();
            var createdAtStr = item["createdAt"]?.ToString();
            
            if (!string.IsNullOrEmpty(labelName) && 
                !string.IsNullOrEmpty(createdAtStr) &&
                DateTimeOffset.TryParse(createdAtStr, out var createdAt))
            {
                labelEvents.Add(new LabelEvent(labelName, createdAt));
            }
        }
    }

    return new IssueData(issueNumber, issueId, labelEvents, state);
}

async Task ProcessIssueData(IssueData issueData)
{
    Console.WriteLine($"Processing issue #{issueData.Number}");
    
    try
    {
        string? lastRelevantLabel = null;
        DateTimeOffset? lastRelevantTime = null;
        
        // Look for the last bug or feature label event
        foreach (var labelEvent in issueData.LabelEvents.OrderBy(le => le.CreatedAt))
        {
            var labelName = labelEvent.LabelName.ToLowerInvariant();
            if (labelName == "bug" || labelName == "enhancement")
            {
                lastRelevantLabel = labelName;
                lastRelevantTime = labelEvent.CreatedAt;
            }
        }
        
        if (lastRelevantLabel != null)
        {
            var issueType = lastRelevantLabel == "bug" ? "BUG" : "FEATURE";
            var issueTypeId = lastRelevantLabel == "bug" ? bugIssueTypeId : featureIssueTypeId;
            Console.WriteLine($"Issue #{issueData.Number}: Found last label '{lastRelevantLabel}' at {lastRelevantTime}, setting type to {issueType}");
            
            if (issueTypeId == null)
            {
                throw new Exception($"Issue type ID not found for {issueType}");
            }
            
            await SetIssueType(issueData.Id, issueTypeId);
            Console.WriteLine($"Successfully updated issue #{issueData.Number}");
        }
        else
        {
            Console.WriteLine($"Issue #{issueData.Number}: No bug/feature label found in history");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error processing issue #{issueData.Number}: {ex.Message}");
    }
}

async Task SetIssueType(string issueId, string issueType)
{
    var mutation = new GraphQLRequest
    {
        Query = """
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
        """,
        Variables = new
        {
            issueId = issueId,
            issueTypeId = issueType
        }
    };
    
    var response = await graphQLClient.SendMutationAsync<JObject>(mutation);
    
    if (response?.Errors != null && response.Errors.Any())
    {
        throw new Exception($"GraphQL mutation failed: {string.Join(", ", response.Errors.Select(e => e.Message))}");
    }
}

// Data structures
record IssueData(int Number, string Id, List<LabelEvent> LabelEvents, string? State = null);
record LabelEvent(string LabelName, DateTimeOffset CreatedAt);

/*
 * PAGE-BY-PAGE PROCESSING IMPLEMENTATION SUMMARY:
 * 
 * 1. Batch Processing Strategy:
 *    - ProcessAllIssues() fetches and processes issues page by page
 *    - FetchIssuesPage() handles single page fetching with pagination
 *    - No memory buildup from storing all issues upfront
 *    - Progress tracking with page numbers and total processed count
 * 
 * 2. Single Issue Processing:
 *    - FetchSingleIssueWithDetails() uses issue-specific query
 *    - Direct issue lookup by number for test mode
 *    - Same parsing and processing logic as batch mode
 * 
 * 3. Memory Efficiency:
 *    - Only loads one page (50 issues) into memory at a time
 *    - Processes each page immediately after fetching
 *    - Suitable for repositories with thousands of issues
 * 
 * 4. Filtering and Processing:
 *    - GraphQL filters: type: null, states: CLOSED
 *    - Client-side filtering: issues with bug/enhancement labels only
 *    - Rate limiting: 1 second delay between issue updates
 * 
 * 5. Progress Monitoring:
 *    - Page-by-page progress logs
 *    - Running total of processed issues
 *    - Clear separation between fetching and processing phases
 * 
 * This approach handles large repositories efficiently without memory issues
 * while providing clear progress feedback during long-running operations.
 */

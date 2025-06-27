using Octokit;
using System.Text.Json;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.SystemTextJson;
using GraphQL;

// if (args.Length < 3)
// {
//     Console.WriteLine("Usage: dotnet run <owner> <repo> <github-token>");
//     Console.WriteLine("Example: dotnet run microsoft dotnet your-github-token");
//     return;
// }

// string owner = args[0];
// string repo = args[1];
// string token = args[2];

string owner = "dotnet";
string repo = "efcore";
string token = "...";

// Testing mode: Set a specific issue ID to test with just one issue
int? testIssueId = null; // Change this to an issue number like: 12345

// Maximum number of issues to process (for testing purposes)
int maxIssueLimit = 0; // Set to 0 or negative for no limit

var processor = new IssueDuplicateProcessor(owner, repo, token);
try
{
    await processor.ProcessDuplicateIssues(testIssueId, maxIssueLimit);
}
finally
{
    processor.Dispose();
}

class IssueDuplicateProcessor : IDisposable
{
    private readonly GitHubClient _client;
    private readonly GraphQLHttpClient _graphQLClient;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _token;
    private const string CLOSED_DUPLICATE_LABEL = "closed-duplicate";
    private readonly string _closedDuplicateLabelNodeId; // Fetch once at startup

    public IssueDuplicateProcessor(string owner, string repo, string token)
    {
        _owner = owner;
        _repo = repo;
        _token = token;
        _client = new GitHubClient(new ProductHeaderValue("IssueDuplicateProcessor"))
        {
            Credentials = new Credentials(token)
        };
        
        // Initialize GraphQL client
        _graphQLClient = new GraphQLHttpClient("https://api.github.com/graphql", new SystemTextJsonSerializer());
        _graphQLClient.HttpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        _graphQLClient.HttpClient.DefaultRequestHeaders.Add("User-Agent", "IssueDuplicateProcessor");
        
        // Fetch the closed-duplicate label node ID once at startup
        _closedDuplicateLabelNodeId = GetLabelNodeId(CLOSED_DUPLICATE_LABEL).GetAwaiter().GetResult();
    }

    public async Task ProcessDuplicateIssues(int? testIssueId = null, int maxIssueLimit = 0)
    {
        if (testIssueId.HasValue)
        {
            Console.WriteLine($"Testing mode: Processing single issue #{testIssueId.Value} in {_owner}/{_repo}...");

            try
            {
                var issue = await _client.Issue.Get(_owner, _repo, testIssueId.Value);

                // Check if the issue has the closed-duplicate label
                bool hasLabel = issue.Labels.Any(l => l.Name == CLOSED_DUPLICATE_LABEL);
                if (!hasLabel)
                {
                    Console.WriteLine($"Warning: Issue #{testIssueId.Value} does not have the '{CLOSED_DUPLICATE_LABEL}' label");
                    return;
                }

                Console.WriteLine($"Found test issue #{issue.Number}: {issue.Title}");
                Console.WriteLine($"Current state: {issue.State}, State reason: {issue.StateReason}");
                Console.WriteLine($"Labels: {string.Join(", ", issue.Labels.Select(l => l.Name))}");
                Console.WriteLine();

                bool success = await ProcessSingleIssue(issue);
                Console.WriteLine($"Test completed. Success: {success}");
                return;
            }
            catch (NotFoundException)
            {
                Console.WriteLine($"Error: Issue #{testIssueId.Value} not found in {_owner}/{_repo}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error retrieving issue #{testIssueId.Value}: {ex.Message}");
            }
        }

        Console.WriteLine($"Processing issues in {_owner}/{_repo}...");

        var issues = await GetIssuesWithClosedDuplicateLabel(maxIssueLimit);
        Console.WriteLine($"Found {issues.Count} issues with '{CLOSED_DUPLICATE_LABEL}' label" + 
                         (maxIssueLimit > 0 ? $" (limited to {maxIssueLimit})" : ""));

        int processedCount = 0;
        foreach (var issue in issues)
        {
            if (await ProcessSingleIssue(issue))
            {
                processedCount++;
            }
        }

        Console.WriteLine($"Successfully processed {processedCount} issues");
    }

    private async Task<List<Issue>> GetIssuesWithClosedDuplicateLabel(int maxResults = 0)
    {
        var searchRequest = new SearchIssuesRequest()
        {
            Labels = new[] { CLOSED_DUPLICATE_LABEL },
            Repos = new RepositoryCollection { $"{_owner}/{_repo}" },
            State = ItemState.Closed
        };

        // Apply limit at the API level if specified
        if (maxResults > 0)
        {
            searchRequest.PerPage = Math.Min(maxResults, 100); // GitHub API max is 100 per page
        }

        var searchResult = await _client.Search.SearchIssues(searchRequest);
        
        // If we need more results than one page can provide, we might need to implement pagination
        // For now, we'll just take what we get from the first page
        var issues = searchResult.Items.ToList();
        
        if (maxResults > 0 && issues.Count > maxResults)
        {
            issues = issues.Take(maxResults).ToList();
        }
        
        return issues;
    }

    private async Task<bool> ProcessSingleIssue(Issue issue)
    {
        try
        {
            Console.WriteLine($"Processing issue #{issue.Number}: {issue.Title}");

            // Check if already closed as duplicate by checking state_reason string value
            if (issue.StateReason?.ToString()?.ToLower() == "duplicate")
            {
                Console.WriteLine($"  Issue #{issue.Number} is already closed as duplicate, removing label only");
                await RemoveClosedDuplicateLabelOnly(issue);
                return true;
            }

            // Update the issue to be closed as duplicate AND remove the label in one call
            await UpdateIssueClosedAsDuplicate(issue);

            Console.WriteLine($"  ✓ Issue #{issue.Number} updated successfully");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ Failed to process issue #{issue.Number}: {ex.Message}");
            return false;
        }
    }

    private async Task UpdateIssueClosedAsDuplicate(Issue issue)
    {
        // Try to find a duplicate issue ID from the issue body or comments
        string? duplicateIssueNodeId = await FindDuplicateIssueId(issue);

        // Use GraphQL mutation to both close the issue as duplicate AND remove the label
        var mutation = new GraphQLRequest
        {
            Query = @"
                mutation($closeInput: CloseIssueInput!, $removeLabelInput: RemoveLabelsFromLabelableInput!) {
                    closeIssue(input: $closeInput) {
                        issue {
                            id
                            number
                            state
                            stateReason
                        }
                    }
                    removeLabelsFromLabelable(input: $removeLabelInput) {
                        labelable {
                            ... on Issue {
                                id
                                number
                            }
                        }
                    }
                }",
            Variables = new
            {
                closeInput = CreateCloseIssueInput(issue.NodeId, duplicateIssueNodeId),
                removeLabelInput = new
                {
                    labelableId = issue.NodeId,
                    labelIds = new[] { _closedDuplicateLabelNodeId }
                }
            }
        };

        var response = await _graphQLClient.SendMutationAsync<CloseIssueMutationResponse>(mutation);

        if (response.Errors != null && response.Errors.Any())
        {
            var errorMessages = string.Join(", ", response.Errors.Select(e => e.Message));
            throw new Exception($"GraphQL errors: {errorMessages}");
        }

        Console.WriteLine($"  Successfully closed issue #{issue.Number} as duplicate and removed label using GraphQL");
    }

    private object CreateCloseIssueInput(string issueNodeId, string? duplicateIssueNodeId)
    {
        var input = new Dictionary<string, object>
        {
            { "issueId", issueNodeId },
            { "stateReason", "DUPLICATE" }
        };

        // Add duplicateIssueId only if we found one
        if (!string.IsNullOrEmpty(duplicateIssueNodeId))
        {
            input["duplicateIssueId"] = duplicateIssueNodeId;
            Console.WriteLine($"  Found duplicate reference, linking to canonical issue");
        }

        return input;
    }

    private async Task<string> GetLabelNodeId(string labelName)
    {
        var query = new GraphQLRequest
        {
            Query = @"
                query($owner: String!, $repo: String!, $labelName: String!) {
                    repository(owner: $owner, name: $repo) {
                        label(name: $labelName) {
                            id
                        }
                    }
                }",
            Variables = new
            {
                owner = _owner,
                repo = _repo,
                labelName = labelName
            }
        };

        var response = await _graphQLClient.SendQueryAsync<LabelQueryResponse>(query);
        
        if (response.Errors != null && response.Errors.Any())
        {
            var errorMessages = string.Join(", ", response.Errors.Select(e => e.Message));
            throw new Exception($"Failed to get label node ID for '{labelName}': {errorMessages}");
        }

        if (response.Data?.Repository?.Label == null)
        {
            throw new Exception($"Label '{labelName}' not found in repository {_owner}/{_repo}");
        }

        return response.Data.Repository.Label.Id;
    }

    private async Task<string?> FindDuplicateIssueId(Issue issue)
    {
        try
        {
            // Check for marked_as_duplicate events first
            var events = await _client.Issue.Events.GetAllForIssue(_owner, _repo, issue.Number);
            var markedAsDuplicateEvent = events.LastOrDefault(e => e.Event.StringValue == "marked_as_duplicate");

            if (markedAsDuplicateEvent is not null)
            {
                Console.WriteLine($"  Found marked_as_duplicate event at {markedAsDuplicateEvent.CreatedAt}");

                // TODO                
                // Get detailed event information via GraphQL using the event's node ID
                var canonicalIssueNodeId = await GetEventDetailsFromGraphQL(markedAsDuplicateEvent.NodeId);
                if (canonicalIssueNodeId != null)
                {
                    Console.WriteLine($"  Found canonical issue via GraphQL event details");
                    return canonicalIssueNodeId;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Could not determine duplicate issue ID: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> GetEventDetailsFromGraphQL(string eventNodeId)
    {
        try
        {
            // Query the event details to find the canonical issue reference
            var query = new GraphQLRequest
            {
                Query = @"
                    query($eventId: ID!) {
                        node(id: $eventId) {
                            ... on MarkedAsDuplicateEvent {
                                canonical {
                                    ... on Issue {
                                        id
                                        number
                                    }
                                }
                            }
                        }
                    }",
                Variables = new { eventId = eventNodeId }
            };

            var response = await _graphQLClient.SendQueryAsync<EventDetailsResponse>(query);
            
            if (response.Errors != null && response.Errors.Any())
            {
                var errorMessages = string.Join(", ", response.Errors.Select(e => e.Message));
                Console.WriteLine($"  GraphQL event query had errors: {errorMessages}");
                return null;
            }

            // Extract the canonical issue node ID from the event
            if (response.Data?.Node?.Canonical?.Id != null)
            {
                Console.WriteLine($"  Found canonical issue #{response.Data.Node.Canonical.Number} from event details");
                return response.Data.Node.Canonical.Id;
            }

            Console.WriteLine($"  Event does not contain canonical issue reference");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  GraphQL event query failed: {ex.Message}");
            return null;
        }
    }

    private async Task RemoveClosedDuplicateLabelOnly(Issue issue)
    {
        try
        {
            await _client.Issue.Labels.RemoveFromIssue(_owner, _repo, issue.Number, CLOSED_DUPLICATE_LABEL);
        }
        catch (NotFoundException)
        {
            // Label might have already been removed, ignore
            Console.WriteLine($"  Label '{CLOSED_DUPLICATE_LABEL}' not found on issue #{issue.Number}");
        }
    }

    public void Dispose()
    {
        _graphQLClient?.Dispose();
    }

    // Response classes for GraphQL queries
    private class LabelQueryResponse
    {
        public RepositoryData? Repository { get; set; }
    }

    private class RepositoryData
    {
        public LabelData? Label { get; set; }
    }

    private class LabelData
    {
        public string Id { get; set; } = string.Empty;
    }

    // Response classes for GraphQL mutations
    private class CloseIssueMutationResponse
    {
        public CloseIssueData? CloseIssue { get; set; }
        public RemoveLabelsData? RemoveLabelsFromLabelable { get; set; }
    }

    private class CloseIssueData
    {
        public IssueData? Issue { get; set; }
    }

    private class RemoveLabelsData
    {
        public LabelableData? Labelable { get; set; }
    }

    private class LabelableData
    {
        public string? Id { get; set; }
        public int? Number { get; set; }
    }

    private class IssueData
    {
        public string Id { get; set; } = string.Empty;
        public int Number { get; set; }
        public string State { get; set; } = string.Empty;
        public string StateReason { get; set; } = string.Empty;
    }

    // Response classes for event details queries
    private class EventDetailsResponse
    {
        public EventNodeData? Node { get; set; }
    }

    private class EventNodeData
    {
        public string? Id { get; set; }
        public DateTime? CreatedAt { get; set; }
        public EventIssueData? Canonical { get; set; }
        public EventIssueData? Duplicate { get; set; }
    }

    private class EventIssueData
    {
        public string? Id { get; set; }
        public int? Number { get; set; }
    }
}

using DataHub.Infrastructure.Tests;

namespace DataHub.Api.Tests;

// xUnit only discovers collection fixtures defined in the test assembly itself.
[CollectionDefinition(SqlCollection.Name)]
public sealed class ApiSqlCollection : ICollectionFixture<SqlDatabaseFixture>;

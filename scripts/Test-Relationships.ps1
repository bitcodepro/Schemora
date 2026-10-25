[CmdletBinding()]
param([string]$Executable,[switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'SmokeSupport.ps1')
if (-not $SkipBuild) { & dotnet build (Join-Path $root 'src/Schemora.Host/Schemora.Host.csproj') -c Debug --nologo; if ($LASTEXITCODE -ne 0) { throw 'Build failed' } }
if (-not $Executable) { $Executable = Join-Path $root 'src/Schemora.Host/bin/Debug/net10.0/Schemora.Host.exe' }
$run = Join-Path $root ('work/relations-' + [guid]::NewGuid().ToString('N')); $fixture = Join-Path $run 'fixture'; New-Item -ItemType Directory -Path $fixture -Force | Out-Null
@'
<Project><Sdk Name="Microsoft.Build.Sql" Version="2.3.0" /><PropertyGroup><Name>RelationDemo</Name><DSP>Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider</DSP></PropertyGroup></Project>
'@ | Set-Content -LiteralPath (Join-Path $fixture 'RelationDemo.sqlproj')
@'
CREATE TABLE dbo.Customers(CustomerId bigint NOT NULL PRIMARY KEY);
CREATE TABLE dbo.Invoices(InvoiceId bigint NOT NULL PRIMARY KEY,CustomerId bigint NOT NULL,CONSTRAINT FK_Invoice_Customer FOREIGN KEY(CustomerId) REFERENCES dbo.Customers(CustomerId));
CREATE TABLE dbo.Transactions(TransactionId nvarchar(80) NOT NULL PRIMARY KEY,InvoiceId bigint NULL);
CREATE TABLE dbo.Documents(DocumentId int NOT NULL PRIMARY KEY,TransactionId nvarchar(80) NULL,CustomerId bigint NULL);
CREATE TABLE dbo.Audit(AuditId int NOT NULL PRIMARY KEY,CustomerId bigint NULL);
'@ | Set-Content -LiteralPath (Join-Path $fixture 'Schema.sql')
@'
SELECT d.* FROM dbo.Documents d INNER JOIN dbo.Transactions t ON d.TransactionId=t.TransactionId;
'@ | Set-Content -LiteralPath (Join-Path $fixture 'Observed.sql')
$server = $null; $checks = 0
try {
    $server = Start-SchemoraTestHost $Executable $run (Join-Path $run 'data')
    $workspace = Invoke-Schemora $server '/api/workspaces' POST @{ name='Relations acceptance'; roots=@($fixture); excludes=@() }
    $job = Invoke-Schemora $server ('/api/workspaces/' + $workspace.id + '/scan') POST @{ mode='full' }; Wait-SchemoraJob $server $job.id | Out-Null
    $base = '/api/workspaces/' + $workspace.id
    $graph = Invoke-Schemora $server ($base + '/graph?limit=2')
    Assert-Schemora ($graph.tables.Count -eq 2 -and $graph.totalTables -eq 5 -and $graph.truncated) 'Graph limit failed'; $checks++
    $tables = Invoke-Schemora $server ($base + '/tables?query=Transaction&limit=100')
    $transaction = @($tables.items | Where-Object name -eq 'Transactions')[0]; $document = @($tables.items | Where-Object name -eq 'Documents')[0]
    Assert-Schemora ($null -ne $transaction -and $null -ne $document) 'Server column/name search failed'; $checks++
    $allTables = Invoke-Schemora $server ($base + '/tables?limit=100')
    $customer = @($allTables.items | Where-Object name -eq 'Customers')[0]; $invoice = @($allTables.items | Where-Object name -eq 'Invoices')[0]
    $edges = @(Invoke-Schemora $server ($base + '/relations'))
    $physical = @($edges | Where-Object kind -eq 'declared-fk')
    Assert-Schemora ($physical.Count -eq 1 -and $physical[0].canUseForJoin) 'Physical FK missing'; $checks++
    $text = @($edges | Where-Object { ($_.fromTableId -eq $document.id -and $_.toTableId -eq $transaction.id) -or ($_.fromTableId -eq $transaction.id -and $_.toTableId -eq $document.id) })[0]
    Assert-Schemora ($null -ne $text -and -not $text.canUseForJoin -and $text.evidence.Count -ge 1) 'Text-ID evidence missing'; $checks++
    $before = @(Invoke-Schemora $server ($base + '/paths') POST @{ fromTableId=$document.id; toTableId=$transaction.id })
    Assert-Schemora ($before.Count -eq 0) 'Unconfirmed relation used in SQL'; $checks++
    $confirmed = Invoke-Schemora $server ($base + '/relations/' + $text.id + '/decision') POST @{ decision='confirmed'; reason='Synthetic exact text-key relation verified' }
    Assert-Schemora ($confirmed.canUseForJoin -and $confirmed.status -eq 'confirmed') 'Confirmation not persisted'; $checks++
    $after = @(Invoke-Schemora $server ($base + '/paths') POST @{ fromTableId=$document.id; toTableId=$transaction.id })
    Assert-Schemora ($after.Count -eq 1 -and $after[0].sql -match 'INNER JOIN' -and $after[0].sql -match '\[TransactionId\]') 'Trusted JOIN failed'; $checks++
    $invoiceCandidate = @($edges | Where-Object { $_.fromTableId -eq $transaction.id -and $_.toTableId -eq $invoice.id })[0]
    Invoke-Schemora $server ($base + '/relations/' + $invoiceCandidate.id + '/decision') POST @{ decision='confirmed'; reason='Synthetic InvoiceId verified' } | Out-Null
    $direct = @($edges | Where-Object { $_.fromTableId -eq $document.id -and $_.toTableId -eq $customer.id })[0]
    Invoke-Schemora $server ($base + '/relations/' + $direct.id + '/decision') POST @{ decision='confirmed'; reason='Synthetic direct customer relation verified' } | Out-Null
    $paths = @(Invoke-Schemora $server ($base + '/paths') POST @{ fromTableId=$document.id; toTableId=$customer.id })
    Assert-Schemora ($paths.Count -ge 2) 'Multiple routes missing'; $checks++
    $focused = Invoke-Schemora $server ($base + '/graph?focusTableId=' + $transaction.id + '&hops=0&limit=100')
    Assert-Schemora ($focused.tables.Count -eq 1 -and $focused.tables[0].id -eq $transaction.id) 'Focus hops0 failed'; $checks++
    $settingKey = 'graph:' + $workspace.id
    Invoke-Schemora $server ('/api/settings/' + $settingKey) PUT @{ value=@{ positions=@{ test=@{x=12;y=34} } } } | Out-Null
    $setting = Invoke-Schemora $server ('/api/settings/' + $settingKey)
    Assert-Schemora ($setting.value.positions.test.x -eq 12) 'Layout persistence failed'; $checks++
    $job = Invoke-Schemora $server ($base + '/scan') POST @{mode='full'}; Wait-SchemoraJob $server $job.id | Out-Null
    $preserved = @(Invoke-Schemora $server ($base + '/relations')) | Where-Object id -eq $text.id
    Assert-Schemora ($preserved.canUseForJoin -and $preserved.status -eq 'confirmed') 'Confirmation lost after rescan'; $checks++
    # Real official-EF semantic acceptance in this run's synthetic work fixture only.
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
  <PropertyGroup Condition="'$(Configuration)' == 'Release'"><DefineConstants>$(DefineConstants);SCHEMORA_SMOKE_RELEASE</DefineConstants></PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.13" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $fixture 'Mapping.csproj') -Encoding utf8
    $mappingPath = Join-Path $fixture 'Mapping.cs'
    @"
using Microsoft.EntityFrameworkCore;
namespace SchemoraSmoke;
public sealed class Customer
{
    public long CustomerId { get; set; }
}
public sealed class Audit
{
    public int AuditId { get; set; }
    public long? CustomerId { get; set; }
}
public sealed class MappingContext : DbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>().ToTable("Customers", "dbo");
        modelBuilder.Entity<Customer>().HasKey(c => c.CustomerId);
        modelBuilder.Entity<Audit>().ToTable("Audit", "dbo");
        modelBuilder.Entity<Audit>().HasKey(a => a.AuditId);
        modelBuilder.Entity<Audit>()
            .HasOne<Customer>()
            .WithMany()
            .HasForeignKey(a => a.CustomerId)
            .HasPrincipalKey(c => c.CustomerId);
    }
}
"@ | Set-Content -LiteralPath $mappingPath -Encoding utf8
    $job = Invoke-Schemora $server ($base + '/scan') POST @{ mode='quick' }
    Wait-SchemoraJob $server $job.id | Out-Null
    $audit = @($allTables.items | Where-Object name -eq 'Audit')[0]
    $efMappings = Invoke-Schemora $server ($base + '/ef-mappings')
    Assert-Schemora ($efMappings.projects.Count -eq 1 -and $efMappings.databases.Count -eq 1 -and $efMappings.projects[0].databaseId -eq $customer.databaseId) 'Single-database EF default assignment missing'; $checks++
    $mappingProjectId = $efMappings.projects[0].projectId
    $explicitBinding = Invoke-Schemora $server ($base + '/ef-mappings') PUT @{ projectId=$mappingProjectId; databaseId=$customer.databaseId }
    $efMappings = Invoke-Schemora $server ($base + '/ef-mappings')
    Assert-Schemora ($explicitBinding.projectId -eq $mappingProjectId -and $explicitBinding.databaseId -eq $customer.databaseId -and $efMappings.projects[0].databaseId -eq $customer.databaseId) 'Explicit EF project/database binding was not accepted'; $checks++
    $efRelations = @(Invoke-Schemora $server ($base + '/relations'))
    $efEdges = @($efRelations | Where-Object { $_.kind -eq 'orm-mapping' -and $_.fromTableId -eq $audit.id -and $_.toTableId -eq $customer.id -and $_.fromColumns.Count -eq 1 -and $_.fromColumns[0] -eq 'CustomerId' -and $_.toColumns.Count -eq 1 -and $_.toColumns[0] -eq 'CustomerId' })
    Assert-Schemora ($efEdges.Count -eq 1 -and -not $efEdges[0].canUseForJoin -and $efEdges[0].status -eq 'candidate' -and @($efEdges[0].evidence | Where-Object { $_.source -eq 'ef-mapping' -and $_.path -eq $mappingPath -and $_.line -ge 1 }).Count -ge 1) 'Partial SDK EF mapping lacks candidate exact edge/path/line evidence'; $checks++
    $contextGraph = Invoke-Schemora $server ($base + '/graph?limit=100')
    Assert-Schemora (@($contextGraph.diagnostics | Where-Object code -eq 'CSHARP_PROJECT_PARTIAL').Count -ge 1) 'SDK project partial diagnostics missing'; $checks++
    # Explicit evaluation only: this repository-owned fixture has no targets or user code.
    # Real installed SDK evaluation proves the chosen configuration changes actual defines.
    $trustedSelection = @{ projectId=$mappingProjectId; configuration='Release'; targetFramework='net10.0'; trustedEvaluation=$true }
    $trustedPreview = Invoke-Schemora $server ($base + '/csharp-context/preview') POST $trustedSelection
    Assert-Schemora (-not $trustedPreview.isComplete -and @($trustedPreview.files | Where-Object { $_.path -eq $mappingPath -and $_.defines -contains 'SCHEMORA_SMOKE_RELEASE' -and $_.defines -notcontains 'DEBUG' }).Count -eq 1) 'Explicit trusted preview ignored Release configuration or claimed full build context'; $checks++
    Invoke-Schemora $server ($base + '/csharp-context') PUT $trustedSelection | Out-Null
    $savedContexts = @(Invoke-Schemora $server ($base + '/csharp-context'))
    Assert-Schemora ($savedContexts.Count -eq 1 -and $savedContexts[0].settings.configuration -eq 'Release' -and $savedContexts[0].settings.trustedEvaluation) 'Trusted Release selection did not persist'; $checks++
    $partialAfterEvaluation = @(Invoke-Schemora $server ($base + '/relations')) | Where-Object id -eq $efEdges[0].id
    Assert-Schemora (-not $partialAfterEvaluation.canUseForJoin -and $partialAfterEvaluation.status -eq 'candidate') 'Evaluation without build targets incorrectly trusted incomplete EF metadata'; $checks++
    $confirmedEf = Invoke-Schemora $server ($base + '/relations/' + $efEdges[0].id + '/decision') POST @{ decision='confirmed'; reason='Synthetic nullable-long EF mapping explicitly reviewed against current schema' }
    Assert-Schemora ($confirmedEf.canUseForJoin -and $confirmedEf.kind -eq 'user-confirmed' -and $confirmedEf.status -eq 'confirmed' -and @($confirmedEf.evidence | Where-Object source -eq 'user-decision').Count -eq 1) 'Explicit EF review did not confer persisted user trust'; $checks++
    $efRelationId = $efEdges[0].id
    $schemaPath = Join-Path $fixture 'Schema.sql'; $sql = Get-Content -LiteralPath $schemaPath -Raw
    Set-Content -LiteralPath $schemaPath -Value ($sql.Replace('TransactionId nvarchar(80) NULL','Unrelated nvarchar(80) NULL'))
    $job = Invoke-Schemora $server ($base + '/scan') POST @{mode='quick'}; Wait-SchemoraJob $server $job.id | Out-Null
    $stale = @(Invoke-Schemora $server ($base + '/relations')) | Where-Object id -eq $text.id
    Assert-Schemora ($stale.status -eq 'stale' -and -not $stale.canUseForJoin) 'Removed column left edge trusted'; $checks++
    # Other confirmed route via Customer/Invoice can still connect these tables; it must omit the stale direct edge.
    $remaining = @(Invoke-Schemora $server ($base + '/paths') POST @{ fromTableId=$document.id; toTableId=$transaction.id })
    Assert-Schemora (@($remaining | Where-Object { $_.relationIds -contains $text.id }).Count -eq 0) 'Stale relation used in path'; $checks++
    # A second real SQL model disables one-DB defaults, proving the explicit EF binding is persisted.
    $secondDirectory = Join-Path $fixture 'SecondDatabase'
    New-Item -ItemType Directory -Path $secondDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $secondDirectory 'Isolation.sqlproj') -Encoding utf8 -Value '<Project><Sdk Name="Microsoft.Build.Sql" Version="2.3.0" /><PropertyGroup><Name>Isolation</Name><DSP>Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider</DSP></PropertyGroup></Project>'
    Set-Content -LiteralPath (Join-Path $secondDirectory 'Schema.sql') -Encoding utf8 -Value 'CREATE TABLE dbo.UnrelatedSmoke(IsolationId int NOT NULL PRIMARY KEY);'
    $job = Invoke-Schemora $server ($base + '/scan') POST @{ mode='quick' }
    Wait-SchemoraJob $server $job.id | Out-Null
    $efMappingsWithTwoDatabases = Invoke-Schemora $server ($base + '/ef-mappings')
    Assert-Schemora ($efMappingsWithTwoDatabases.databases.Count -eq 2 -and $efMappingsWithTwoDatabases.projects[0].databaseId -eq $customer.databaseId) 'Explicit EF binding became ambiguous after discovering a second database'; $checks++
    $efWithTwoDatabases = @(Invoke-Schemora $server ($base + '/relations')) | Where-Object id -eq $efRelationId
    Assert-Schemora ($efWithTwoDatabases.kind -eq 'user-confirmed' -and $efWithTwoDatabases.canUseForJoin) 'Explicit EF binding not used when multiple database models exist'; $checks++
    Stop-SchemoraTestHost $server; $server = $null
    $server = Start-SchemoraTestHost $Executable $run (Join-Path $run 'data')
    $persisted = @(Invoke-Schemora $server ($base + '/relations')) | Where-Object id -eq $text.id
    Assert-Schemora ($persisted.status -eq 'stale') 'Decision lost after restart'; $checks++
    $efMappingAfterRestart = Invoke-Schemora $server ($base + '/ef-mappings')
    Assert-Schemora ($efMappingAfterRestart.databases.Count -eq 2 -and $efMappingAfterRestart.projects[0].projectId -eq $mappingProjectId -and $efMappingAfterRestart.projects[0].databaseId -eq $customer.databaseId) 'Explicit EF project/database binding lost after restart'; $checks++
    $efAfterRestart = @(Invoke-Schemora $server ($base + '/relations')) | Where-Object id -eq $efRelationId
    Assert-Schemora ($efAfterRestart.kind -eq 'user-confirmed' -and $efAfterRestart.status -eq 'confirmed' -and $efAfterRestart.canUseForJoin -and @($efAfterRestart.evidence | Where-Object { $_.path -eq $mappingPath -and $_.line -ge 1 }).Count -ge 1) 'User-confirmed EF edge/evidence did not survive restart with two databases'; $checks++
    @{ success=$true; checks=$checks; tables=5; textRelationConfirmed=$true; unconfirmedBlocked=$true; staleBlocked=$true; multiplePaths=$paths.Count; restart=$true; efChecked=$true; efNullableForeignKey=$true; efBindingPersistedWithTwoDatabases=$true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'work/relationships-report.json')
    Write-Output "PASS Phase2 relationships: $checks checks"
} finally { Stop-SchemoraTestHost $server }




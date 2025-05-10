using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using System.Diagnostics.Metrics;
using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel for HTTPS in development
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(5179); // HTTP
    serverOptions.ListenAnyIP(7170, listenOptions =>
    {
        listenOptions.UseHttps();
    });
});

// Configura��o da Observabilidade
ConfigureObservability(builder);

// Configura��o dos servi�os
ConfigureServices(builder);

var app = builder.Build();

// Configura��o do pipeline HTTP
ConfigurePipeline(app);

// Configura��o dos endpoints
ConfigureEndpoints(app);

app.Run();

// M�todos de configura��o
static void ConfigureObservability(WebApplicationBuilder builder)
{
    // Configura��o do OpenTelemetry
    builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation()
               .AddSqlClientInstrumentation()
               .AddOtlpExporter(o =>
               {
                   o.Endpoint = new Uri("http://localhost:4317"); // Porta OTLP gRPC
               });
    });

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource
            .AddService(serviceName: builder.Environment.ApplicationName))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSqlClientInstrumentation()
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri("http://localhost:4318");
            }))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri("http://localhost:4318");
            }));

    // Configura��o do logging com OpenTelemetry
    builder.Logging.AddOpenTelemetry(logging => logging
        .AddOtlpExporter());
}

static void ConfigureServices(WebApplicationBuilder builder)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // Configura��o do Dapper
    builder.Services.AddScoped<SqlConnection>(_ =>
        new SqlConnection(builder.Configuration.GetConnectionString("DefaultConnection")));

    // Registrar o reposit�rio
    builder.Services.AddScoped<IVooRepository, VooRepository>();

    // Health Checks
    builder.Services.AddHealthChecks()
        .AddCheck<SqlServerHealthCheck>("sql-server",
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "database", "sql" });
}

static void ConfigurePipeline(WebApplication app)
{
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    // Middleware de m�tricas deve vir antes do UseHttpsRedirection
    app.UseHttpMetrics();

    app.UseHttpsRedirection();

    app.MapMetrics(); // Endpoint /metrics para Prometheus

    // Health Check endpoints
    app.MapHealthChecks("/health");
    app.MapHealthChecks("/health-https").RequireHost("*:7170"); // HTTPS specific

    // Endpoint customizado para m�tricas
    app.MapGet("/health-metrics", async (HealthCheckService healthCheckService) =>
    {
        var report = await healthCheckService.CheckHealthAsync();
        return Results.Json(new
        {
            status = report.Status.ToString(),
            results = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description
            })
        });
    });
}

static void ConfigureEndpoints(WebApplication app)
{
    // Health endpoints are already configured in ConfigurePipeline

    // API endpoints
    app.MapGet("/voos", async (
        [FromServices] IVooRepository repo,
        [FromServices] ILogger<Program> logger,
        [FromQuery] string? origem,
        [FromQuery] string? destino,
        [FromQuery] DateTime? data) =>
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("BuscarVoos");
        activity?.SetTag("origem", origem);
        activity?.SetTag("destino", destino);
        activity?.SetTag("data", data?.ToString("yyyy-MM-dd"));

        logger.LogInformation("Iniciando busca por voos - Origem: {Origem}, Destino: {Destino}, Data: {Data}",
            origem, destino, data);

        try
        {
            var voos = await repo.BuscarVoos(origem, destino, data);
            logger.LogInformation("Busca conclu�da - {Quantidade} voos encontrados", voos.Count());
            MetricsConfig.VoosPesquisadosCounter.Add(voos.Count());
            return Results.Ok(voos);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao buscar voos");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            MetricsConfig.ErrosBuscaVoosCounter.Add(1);
            return Results.Problem("Erro ao buscar voos", statusCode: 500);
        }
    })
    .WithName("GetVoos")
    .WithOpenApi()
    .Produces<IEnumerable<Voo>>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status500InternalServerError);

    app.MapGet("/voos/baratos", async (
        [FromServices] IVooRepository repo,
        [FromServices] ILogger<Program> logger,
        [FromQuery] string origem,
        [FromQuery] string destino) =>
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("BuscarVooMaisBarato");
        activity?.SetTag("origem", origem);
        activity?.SetTag("destino", destino);

        logger.LogInformation("Buscando voo mais barato - Origem: {Origem}, Destino: {Destino}", origem, destino);

        try
        {
            var voo = await repo.BuscarVooMaisBarato(origem, destino);
            if (voo is null)
            {
                logger.LogWarning("Nenhum voo encontrado - Origem: {Origem}, Destino: {Destino}", origem, destino);
                MetricsConfig.VoosNaoEncontradosCounter.Add(1);
                return Results.NotFound();
            }

            logger.LogInformation("Voo mais barato encontrado - Valor: {Valor}", voo.Valor);
            MetricsConfig.VoosMaisBaratosPesquisadosCounter.Add(1);
            return Results.Ok(voo);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao buscar voo mais barato");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            MetricsConfig.ErrosBuscaVoosCounter.Add(1);
            return Results.Problem("Erro ao buscar voo mais barato", statusCode: 500);
        }
    })
    .WithName("GetVooMaisBarato")
    .WithOpenApi()
    .Produces<Voo>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status404NotFound)
    .Produces(StatusCodes.Status500InternalServerError);

    app.MapPost("/chat", async (
        [FromServices] IVooRepository repo,
        [FromServices] ILogger<Program> logger,
        [FromBody] ChatRequest request) =>
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("ProcessarMensagemChat");
        activity?.SetTag("mensagem", request.Mensagem);

        logger.LogInformation("Processando mensagem de chat: {Mensagem}", request.Mensagem);

        try
        {
            var response = await repo.ProcessarMensagemChat(request.Mensagem);
            logger.LogInformation("Mensagem processada com sucesso");
            MetricsConfig.MensagensChatProcessadasCounter.Add(1);
            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao processar mensagem de chat");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            MetricsConfig.ErrosProcessamentoChatCounter.Add(1);
            return Results.Problem("Erro ao processar mensagem de chat", statusCode: 500);
        }
    })
    .WithName("ProcessarMensagemChat")
    .WithOpenApi()
    .Produces<ChatResponse>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status500InternalServerError);
}

[ApiController]
[Route("api/[controller]")]
public class VoosController : ControllerBase
{
    private readonly IVooRepository _repo;
    private readonly ILogger<VoosController> _logger;

    public VoosController(IVooRepository repo, ILogger<VoosController> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetVoos(
        [FromQuery] string? origem,
        [FromQuery] string? destino,
        [FromQuery] DateTime? data)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("BuscarVoos");
        activity?.SetTag("origem", origem);
        activity?.SetTag("destino", destino);
        activity?.SetTag("data", data?.ToString("yyyy-MM-dd"));

        _logger.LogInformation("Iniciando busca por voos - Origem: {Origem}, Destino: {Destino}, Data: {Data}",
            origem, destino, data);

        try
        {
            var voos = await _repo.BuscarVoos(origem, destino, data);
            MetricsConfig.VoosPesquisadosCounter.Add(voos.Count());
            return Ok(voos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar voos");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            MetricsConfig.ErrosBuscaVoosCounter.Add(1);
            throw;
        }
    }

    [HttpGet("baratos")]
    public async Task<IActionResult> GetVooMaisBarato(
        [FromQuery] string origem,
        [FromQuery] string destino)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("BuscarVooMaisBarato");
        activity?.SetTag("origem", origem);
        activity?.SetTag("destino", destino);

        _logger.LogInformation("Buscando voo mais barato - Origem: {Origem}, Destino: {Destino}", origem, destino);

        try
        {
            var voo = await _repo.BuscarVooMaisBarato(origem, destino);
            if (voo is null)
            {
                MetricsConfig.VoosNaoEncontradosCounter.Add(1);
                return NotFound();
            }

            MetricsConfig.VoosMaisBaratosPesquisadosCounter.Add(1);
            return Ok(voo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar voo mais barato");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            MetricsConfig.ErrosBuscaVoosCounter.Add(1);
            throw;
        }
    }

    [HttpPost("chat")]
    public async Task<IActionResult> ProcessarMensagemChat([FromBody] ChatRequest request)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("ProcessarMensagemChat");
        activity?.SetTag("mensagem", request.Mensagem);

        _logger.LogInformation("Processando mensagem de chat: {Mensagem}", request.Mensagem);

        try
        {
            var response = await _repo.ProcessarMensagemChat(request.Mensagem);
            MetricsConfig.MensagensChatProcessadasCounter.Add(1);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao processar mensagem de chat");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            MetricsConfig.ErrosProcessamentoChatCounter.Add(1);
            throw;
        }
    }
}

// Configura��es de m�tricas e diagn�sticos
public static class DiagnosticsConfig
{
    public const string ServiceName = "VooService";
    public static ActivitySource ActivitySource = new ActivitySource(ServiceName);
}

public static class MetricsConfig
{
    private static readonly Meter Meter = new(DiagnosticsConfig.ServiceName);

    public static Counter<int> VoosPesquisadosCounter = Meter.CreateCounter<int>("voos_pesquisados_total", "Total de voos pesquisados");
    public static Counter<int> VoosMaisBaratosPesquisadosCounter = Meter.CreateCounter<int>("voos_mais_baratos_pesquisados_total", "Total de voos mais baratos pesquisados");
    public static Counter<int> VoosNaoEncontradosCounter = Meter.CreateCounter<int>("voos_nao_encontrados_total", "Total de voos n�o encontrados");
    public static Counter<int> MensagensChatProcessadasCounter = Meter.CreateCounter<int>("mensagens_chat_processadas_total", "Total de mensagens de chat processadas");
    public static Counter<int> ErrosBuscaVoosCounter = Meter.CreateCounter<int>("erros_busca_voos_total", "Total de erros na busca de voos");
    public static Counter<int> ErrosProcessamentoChatCounter = Meter.CreateCounter<int>("erros_processamento_chat_total", "Total de erros no processamento de chat");
}

// Modelos e interfaces
public record Voo
{
    public int Id { get; init; }
    public string Origem { get; init; } = string.Empty;
    public string Destino { get; init; } = string.Empty;
    public DateTime DataPartida { get; init; }
    public DateTime DataChegada { get; init; }
    public decimal Valor { get; init; }
    public string CompanhiaAerea { get; init; } = string.Empty;
    public int AssentosDisponiveis { get; init; }
}

public record ChatRequest(string Mensagem);
public record ChatResponse(string Resposta, IEnumerable<Voo>? Voos = null);

public interface IVooRepository
{
    Task<IEnumerable<Voo>> BuscarVoos(string? origem, string? destino, DateTime? data);
    Task<Voo?> BuscarVooMaisBarato(string origem, string destino);
    Task<ChatResponse> ProcessarMensagemChat(string mensagem);
}

public class VooRepository : IVooRepository
{
    private readonly SqlConnection _db;
    private readonly ILogger<VooRepository> _logger;

    public VooRepository(SqlConnection db, ILogger<VooRepository> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IEnumerable<Voo>> BuscarVoos(string? origem, string? destino, DateTime? data)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("BuscarVoosDatabase");

        var sql = "SELECT * FROM Voos WHERE 1=1";
        var parameters = new DynamicParameters();

        if (!string.IsNullOrEmpty(origem))
        {
            sql += " AND Origem = @Origem";
            parameters.Add("Origem", origem);
        }

        if (!string.IsNullOrEmpty(destino))
        {
            sql += " AND Destino = @Destino";
            parameters.Add("Destino", destino);
        }

        if (data.HasValue)
        {
            sql += " AND CONVERT(DATE, DataPartida) = @Data";
            parameters.Add("Data", data.Value.Date);
        }

        sql += " ORDER BY DataPartida";

        _logger.LogDebug("Executando consulta SQL: {Sql}", sql);
        return await _db.QueryAsync<Voo>(sql, parameters);
    }

    public async Task<Voo?> BuscarVooMaisBarato(string origem, string destino)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("BuscarVooMaisBaratoDatabase");

        var sql = @"SELECT TOP 1 * FROM Voos 
                   WHERE Origem = @Origem AND Destino = @Destino 
                   ORDER BY Valor";

        _logger.LogDebug("Executando consulta SQL: {Sql}", sql);
        return await _db.QueryFirstOrDefaultAsync<Voo>(sql, new { Origem = origem, Destino = destino });
    }

    public async Task<ChatResponse> ProcessarMensagemChat(string mensagem)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("ProcessarMensagemChatBusiness");

        _logger.LogDebug("Processando mensagem: {Mensagem}", mensagem);
        mensagem = mensagem.ToLower();

        if (IsSaudacao(mensagem))
            return new ChatResponse("Ol�! Sou o assistente virtual de passagens a�reas. Como posso ajudar?");

        if (IsBuscaPorVooBarato(mensagem))
            return await ProcessarBuscaVooBarato(mensagem);

        if (IsBuscaPorVoos(mensagem))
            return await ProcessarBuscaVoos(mensagem);

        return new ChatResponse("Desculpe, n�o entendi. Voc� pode perguntar sobre voos dispon�veis ou o voo mais barato para um destino?");
    }

    private bool IsSaudacao(string mensagem) =>
        mensagem.Contains("oi") || mensagem.Contains("ol�") || mensagem.Contains("ola") ||
        mensagem.Contains("bom dia") || mensagem.Contains("boa tarde") || mensagem.Contains("boa noite");

    private bool IsBuscaPorVooBarato(string mensagem) =>
        mensagem.Contains("barat") || mensagem.Contains("econ�mi") || mensagem.Contains("economi");

    private bool IsBuscaPorVoos(string mensagem) =>
        mensagem.Contains("voos") || mensagem.Contains("passagens");

    private async Task<ChatResponse> ProcessarBuscaVooBarato(string mensagem)
    {
        var (origem, destino) = ExtrairOrigemDestino(mensagem);
        if (string.IsNullOrEmpty(origem) || string.IsNullOrEmpty(destino))
            return new ChatResponse("Por favor, informe a origem e o destino do voo.");

        var voo = await BuscarVooMaisBarato(origem, destino);
        return voo is null
            ? new ChatResponse($"N�o encontrei voos dispon�veis de {origem} para {destino}.")
            : new ChatResponse($"O voo mais barato de {origem} para {destino} � da {voo.CompanhiaAerea} no valor de R${voo.Valor} no dia {voo.DataPartida:dd/MM/yyyy}.", new[] { voo });
    }

    private async Task<ChatResponse> ProcessarBuscaVoos(string mensagem)
    {
        var origem = ExtrairParametro(mensagem, "de", "para");
        var destino = ExtrairParametro(mensagem, "para", "no dia");
        var dataStr = ExtrairParametro(mensagem, "no dia", null);

        if (string.IsNullOrEmpty(origem) || string.IsNullOrEmpty(destino))
            return new ChatResponse("Por favor, informe a origem e o destino do voo.");

        DateTime? data = DateTime.TryParse(dataStr, out var parsedDate) ? parsedDate : null;
        var voos = await BuscarVoos(origem, destino, data);

        return !voos.Any()
            ? new ChatResponse("N�o encontrei voos com os crit�rios informados.")
            : new ChatResponse($"Encontrei {voos.Count()} voos dispon�veis:", voos);
    }

    private (string? origem, string? destino) ExtrairOrigemDestino(string mensagem)
    {
        var partes = mensagem.Split(new[] { "para" }, StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length != 2) return (null, null);

        var origem = partes[0]
            .Replace("qual o voo mais barato de", "")
            .Replace("quero o voo mais barato de", "")
            .Trim();

        var destino = partes[1].Trim();
        return (origem, destino);
    }

    private string? ExtrairParametro(string mensagem, string inicio, string? fim)
    {
        var startIndex = mensagem.IndexOf(inicio);
        if (startIndex == -1) return null;

        startIndex += inicio.Length;
        var endIndex = fim != null ? mensagem.IndexOf(fim, startIndex) : -1;

        return endIndex == -1
            ? mensagem.Substring(startIndex).Trim()
            : mensagem.Substring(startIndex, endIndex - startIndex).Trim();
    }
}

public class SqlServerHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    public SqlServerHealthCheck(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection");
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteScalarAsync("SELECT 1");
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                description: "Falha na conex�o com o SQL Server",
                exception: ex);
        }
    }
}
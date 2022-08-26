using Grpc.Core;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Protos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MareSynchronosStaticFilesServer;

public class FileService : MareSynchronosShared.Protos.FileService.FileServiceBase
{
    private readonly string _basePath;
    private readonly MareDbContext _mareDbContext;
    private readonly ILogger<FileService> _logger;
    private readonly MareMetrics _metricsClient;

    public FileService(MareDbContext mareDbContext, IConfiguration configuration, ILogger<FileService> logger, MareMetrics metricsClient)
    {
        _basePath = configuration.GetRequiredSection("MareSynchronos")["CacheDirectory"];
        _mareDbContext = mareDbContext;
        _logger = logger;
        _metricsClient = metricsClient;
    }

    public override async Task<Empty> UploadFile(UploadFileRequest request, ServerCallContext context)
    {
        var filePath = Path.Combine(_basePath, request.Hash);
        var file = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == request.Hash && f.UploaderUID == request.Uploader);
        if (file != null)
        {
            var byteData = request.FileData.ToArray();
            await File.WriteAllBytesAsync(filePath, byteData);
            file.Uploaded = true;

            _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotal, 1);
            _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotalSize, byteData.Length);

            await _mareDbContext.SaveChangesAsync().ConfigureAwait(false);
            _logger.LogInformation("User {user} uploaded file {hash}", request.Uploader, request.Hash);
        }

        return new Empty();
    }

    public override async Task<Empty> DeleteFiles(DeleteFilesRequest request, ServerCallContext context)
    {
        foreach (var hash in request.Hash)
        {
            try
            {
                FileInfo fi = new FileInfo(Path.Combine(_basePath, hash));
                fi.Delete();
                var file = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
                if (file != null)
                {
                    _mareDbContext.Files.Remove(file);

                    _metricsClient.DecGauge(MetricsAPI.GaugeFilesTotal, 1);
                    _metricsClient.DecGauge(MetricsAPI.GaugeFilesTotalSize, fi.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not delete file for hash {hash}", hash);
            }
        }

        await _mareDbContext.SaveChangesAsync().ConfigureAwait(false);
        return new Empty();
    }

    public override Task<FileSizeResponse> GetFileSizes(FileSizeRequest request, ServerCallContext context)
    {
        FileSizeResponse response = new();
        foreach (var hash in request.Hash.Distinct())
        {
            FileInfo fi = new(Path.Combine(_basePath, hash));
            if (fi.Exists)
            {
                response.HashToFileSize.Add(hash, fi.Length);
            }
            else
            {
                response.HashToFileSize.Add(hash, 0);
            }
        }

        return Task.FromResult(response);
    }
}

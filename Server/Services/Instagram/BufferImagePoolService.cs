using System.Collections.Concurrent;

namespace Server.Services.Instagram
{
    /// <summary>
    /// Manages a fixed pool of 10 permanent URLs for Buffer image publishing.
    /// Images are stored in a circular buffer - oldest images are replaced when the pool is full.
    /// This ensures Buffer can always access images via stable URLs that never expire.
    /// </summary>
    public class BufferImagePoolService
    {
        private const int PoolSize = 10;
        private readonly string _poolDirectory;
        private readonly ILogger<BufferImagePoolService> _logger;
        private int _nextSlot = 0;
        private readonly object _lock = new();
        
        // Track which slots are occupied and their metadata
        private readonly ConcurrentDictionary<int, ImageSlotInfo> _slots = new();

        public BufferImagePoolService(IWebHostEnvironment env, ILogger<BufferImagePoolService> logger)
        {
            _logger = logger;
            _poolDirectory = Path.Combine(env.ContentRootPath, "GeneratedMedia", "buffer-pool");
            
            // Ensure the pool directory exists
            Directory.CreateDirectory(_poolDirectory);
            
            Console.WriteLine($"[BufferPool] ✓ Service initialized with {PoolSize} slots");
            Console.WriteLine($"[BufferPool] ✓ Pool directory: {_poolDirectory}");
            _logger.LogInformation("[BufferPool] Initialized with {PoolSize} slots at {Directory}", PoolSize, _poolDirectory);
            
            // Load existing slots on startup
            LoadExistingSlots();
        }

        /// <summary>
        /// Allocates a slot in the pool for a new image. Returns the slot number and public URL.
        /// Uses a circular buffer - when full, replaces the oldest image.
        /// </summary>
        public (int slot, string url) AllocateSlot(byte[] imageData, string fileName, string contentType, string publicBaseUrl)
        {
            lock (_lock)
            {
                var slot = _nextSlot;
                _nextSlot = (_nextSlot + 1) % PoolSize; // Circular increment

                // Determine file extension from content type or filename
                var extension = GetExtensionFromContentType(contentType);
                if (string.IsNullOrEmpty(extension))
                {
                    var fileExt = Path.GetExtension(fileName);
                    extension = string.IsNullOrEmpty(fileExt) ? ".jpg" : fileExt;
                }

                var slotFileName = $"{slot}{extension}";
                var filePath = Path.Combine(_poolDirectory, slotFileName);

                // Write the image to disk
                Console.WriteLine($"[BufferPool] Writing {imageData.Length} bytes to {filePath}");
                File.WriteAllBytes(filePath, imageData);
                Console.WriteLine($"[BufferPool] ✓ File written successfully");

                // Update slot metadata
                _slots[slot] = new ImageSlotInfo
                {
                    Slot = slot,
                    FileName = slotFileName,
                    FilePath = filePath,
                    ContentType = contentType,
                    OriginalFileName = fileName,
                    CreatedAt = DateTime.UtcNow,
                    FileSize = imageData.Length
                };

                // Build the public URL with a unique version token so Buffer never
                // treats a reused slot as duplicate content from a previous post.
                var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var url = $"{publicBaseUrl.TrimEnd('/')}/api/public/buffer-image/{slot}?v={version}";

                Console.WriteLine($"[BufferPool] ✓ Allocated slot {slot}: {fileName} → {url}");
                _logger.LogInformation(
                    "[BufferPool] Allocated slot {Slot} for {FileName} ({Size} bytes) - URL: {Url}",
                    slot, fileName, imageData.Length, url);

                return (slot, url);
            }
        }

        /// <summary>
        /// Gets the image data for a specific slot.
        /// </summary>
        public (byte[]? data, string? contentType, string? fileName) GetSlotImage(int slot)
        {
            if (slot < 0 || slot >= PoolSize)
            {
                _logger.LogWarning("[BufferPool] Invalid slot requested: {Slot}", slot);
                return (null, null, null);
            }

            if (!_slots.TryGetValue(slot, out var info))
            {
                _logger.LogWarning("[BufferPool] Slot {Slot} not found", slot);
                return (null, null, null);
            }

            if (!File.Exists(info.FilePath))
            {
                _logger.LogWarning("[BufferPool] File not found for slot {Slot}: {Path}", slot, info.FilePath);
                _slots.TryRemove(slot, out _);
                return (null, null, null);
            }

            try
            {
                var data = File.ReadAllBytes(info.FilePath);
                return (data, info.ContentType, info.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BufferPool] Error reading slot {Slot}", slot);
                return (null, null, null);
            }
        }

        /// <summary>
        /// Gets information about all occupied slots.
        /// </summary>
        public List<ImageSlotInfo> GetAllSlots()
        {
            return _slots.Values.OrderBy(s => s.Slot).ToList();
        }

        /// <summary>
        /// Clears all slots and deletes all images.
        /// </summary>
        public void ClearAll()
        {
            lock (_lock)
            {
                foreach (var info in _slots.Values)
                {
                    try
                    {
                        if (File.Exists(info.FilePath))
                            File.Delete(info.FilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[BufferPool] Error deleting file: {Path}", info.FilePath);
                    }
                }

                _slots.Clear();
                _nextSlot = 0;
                _logger.LogInformation("[BufferPool] All slots cleared");
            }
        }

        private void LoadExistingSlots()
        {
            try
            {
                var files = Directory.GetFiles(_poolDirectory);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                    
                    if (int.TryParse(nameWithoutExt, out var slot) && slot >= 0 && slot < PoolSize)
                    {
                        var extension = Path.GetExtension(fileName);
                        var contentType = GetContentTypeFromExtension(extension);
                        var fileInfo = new FileInfo(file);

                        _slots[slot] = new ImageSlotInfo
                        {
                            Slot = slot,
                            FileName = fileName,
                            FilePath = file,
                            ContentType = contentType,
                            OriginalFileName = fileName,
                            CreatedAt = fileInfo.CreationTimeUtc,
                            FileSize = fileInfo.Length
                        };

                        _logger.LogInformation("[BufferPool] Loaded existing slot {Slot}: {FileName}", slot, fileName);
                        
                        // Update next slot to be after the highest found
                        if (slot >= _nextSlot)
                            _nextSlot = (slot + 1) % PoolSize;
                    }
                }

                if (_slots.Count > 0)
                {
                    _logger.LogInformation("[BufferPool] Loaded {Count} existing slots, next slot: {Next}", _slots.Count, _nextSlot);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BufferPool] Error loading existing slots");
            }
        }

        private static string GetExtensionFromContentType(string contentType)
        {
            return contentType.ToLowerInvariant() switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "image/bmp" => ".bmp",
                "video/mp4" => ".mp4",
                "video/quicktime" => ".mov",
                _ => ""
            };
        }

        private static string GetContentTypeFromExtension(string extension)
        {
            return extension.ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".mp4" => "video/mp4",
                ".mov" => "video/quicktime",
                _ => "application/octet-stream"
            };
        }
    }

    public class ImageSlotInfo
    {
        public int Slot { get; set; }
        public string FileName { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string ContentType { get; set; } = "";
        public string OriginalFileName { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public long FileSize { get; set; }
    }
}

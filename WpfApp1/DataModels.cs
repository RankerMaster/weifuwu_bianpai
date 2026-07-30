namespace WpfApp1
{
    public sealed class DeviceInfo
    {
        public DeviceInfo(string name, string ip, double cpuUsage, double memoryUsage, double diskUsage, int imageCount, int containerCount, string contextName = "")
        {
            Name = name;
            Ip = ip;
            CpuUsage = cpuUsage;
            MemoryUsage = memoryUsage;
            DiskUsage = diskUsage;
            ImageCount = imageCount;
            ContainerCount = containerCount;
            ContextName = contextName;
        }

        public string Name { get; }
        public string Ip { get; }
        public double CpuUsage { get; }
        public double MemoryUsage { get; }
        public double DiskUsage { get; }
        public int ImageCount { get; }
        public int ContainerCount { get; }
        public string ContextName { get; }
    }

    public sealed class DeviceImageRow
    {
        public DeviceImageRow(string deviceIp, string repository, string tag, string created, string size, string labels, string imageId, string chineseName, string deviceName = "", string contextName = "")
        {
            DeviceIp = deviceIp;
            Repository = repository;
            Tag = tag;
            Created = created;
            Size = size;
            Labels = labels;
            ImageId = imageId;
            ChineseName = string.IsNullOrWhiteSpace(chineseName) ? repository : chineseName;
            DeviceName = deviceName;
            ContextName = contextName;
        }

        public string DeviceIp { get; }
        public string Name => Repository;
        public string Repository { get; }
        public string Tag { get; }
        public string Created { get; }
        public string Size { get; }
        public string Labels { get; }
        public string ImageId { get; }
        public string ChineseName { get; set; }
        public string DeviceName { get; }
        public string ContextName { get; }
    }

    public sealed class DeployImageCard
    {
        public DeployImageCard(string name, string id, string created, string size)
        {
            RepoTag = name;
            Id = id;
            Created = created;
            Size = size;
        }

        public string RepoTag { get; }
        public string Name
        {
            get
            {
                var repoTag = RepoTag ?? string.Empty;
                var slash = repoTag.LastIndexOf('/');
                var colon = repoTag.LastIndexOf(':');
                if (colon > slash)
                {
                    return repoTag.Substring(0, colon);
                }

                return repoTag;
            }
        }
        public string Tag
        {
            get
            {
                var repoTag = RepoTag ?? string.Empty;
                var slash = repoTag.LastIndexOf('/');
                var colon = repoTag.LastIndexOf(':');
                if (colon > slash && colon < repoTag.Length - 1)
                {
                    return repoTag.Substring(colon + 1);
                }

                return "latest";
            }
        }
        public string ImageId => Id;
        public string Id { get; }
        public string Created { get; }
        public string Size { get; }
    }

    public sealed class ImageComposeRow
    {
        public ImageComposeRow(string repoTag, string imageId, string created, string sizeMb, string deviceName = "", string contextName = "", string chineseName = "")
        {
            RepoTag = repoTag;
            ImageId = imageId;
            Created = created;
            SizeMb = sizeMb;
            DeviceName = deviceName;
            ContextName = contextName;
            ChineseName = string.IsNullOrWhiteSpace(chineseName) ? Name : chineseName;
        }

        public string RepoTag { get; }
        public string Name
        {
            get
            {
                var repoTag = RepoTag ?? string.Empty;
                var slash = repoTag.LastIndexOf('/');
                var colon = repoTag.LastIndexOf(':');
                if (colon > slash)
                {
                    return repoTag.Substring(0, colon);
                }

                return repoTag;
            }
        }
        public string Tag
        {
            get
            {
                var repoTag = RepoTag ?? string.Empty;
                var slash = repoTag.LastIndexOf('/');
                var colon = repoTag.LastIndexOf(':');
                if (colon > slash && colon < repoTag.Length - 1)
                {
                    return repoTag.Substring(colon + 1);
                }

                return "latest";
            }
        }
        public string ImageId { get; }
        public string Created { get; }
        public string SizeMb { get; }
        public string DeviceName { get; }
        public string ContextName { get; }
        public string ChineseName { get; set; }
    }

    public sealed class ProgramFileRow
    {
        public ProgramFileRow(string name, string size, string modifiedAt, string sourcePath = "")
        {
            Name = name;
            Size = size;
            ModifiedAt = modifiedAt;
            SourcePath = sourcePath;
        }

        public string Name { get; }
        public string Size { get; }
        public string ModifiedAt { get; }
        public string SourcePath { get; }
    }

    public sealed class ContainerComposeRow
    {
        public ContainerComposeRow(
            string deviceName,
            string id,
            string name,
            string chineseName,
            string image,
            string status,
            string ports,
            string cpuCores,
            string cpuPercent,
            string memoryUsage,
            string memoryPercent,
            string diskReadWrite,
            string detail,
            string fullId = "",
            string size = "-")
        {
            DeviceName = deviceName;
            Id = id;
            Name = name;
            ChineseName = chineseName;
            Image = image;
            Status = status;
            Ports = ports;
            CpuCores = string.IsNullOrWhiteSpace(cpuCores) ? "-" : cpuCores;
            CpuPercent = cpuPercent;
            MemoryUsage = memoryUsage;
            MemoryPercent = memoryPercent;
            DiskReadWrite = diskReadWrite;
            Detail = detail;
            FullId = string.IsNullOrWhiteSpace(fullId) ? id : fullId;
            Size = string.IsNullOrWhiteSpace(size) ? "-" : NormalizeMemoryUnitText(size);
        }

        public string DeviceName { get; }
        public string Id { get; }
        public string Name { get; }
        public string ChineseName { get; }
        public string Image { get; }
        public string Status { get; }
        public string Ports { get; }
        public string CpuCores { get; }
        public string CpuPercent { get; }
        public string MemoryUsage { get; }
        public string Size { get; }
        public string MemoryPercent { get; }
        public string DiskReadWrite { get; }
        public string Detail { get; }
        public string FullId { get; }

        private static string NormalizeMemoryUnitText(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return "-";
            }

            return text
                .Replace("KiB", "KB")
                .Replace("KIB", "KB")
                .Replace("MiB", "MB")
                .Replace("MIB", "MB")
                .Replace("GiB", "GB")
                .Replace("GIB", "GB")
                .Replace("TiB", "TB")
                .Replace("TIB", "TB");
        }
    }
}

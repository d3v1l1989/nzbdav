namespace NzbWebDAV.Clients.Usenet.Contexts;

public record SparseFileCacheContext(Guid MediaFileId, long FileSize);

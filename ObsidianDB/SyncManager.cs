using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ObsidianDB.Logging;

namespace ObsidianDB;

/// <summary>
/// Manages synchronization between the Obsidian vault and the vector database.
/// </summary>
/// <remarks>
/// <para>
/// The SyncManager class is responsible for maintaining synchronization between the Obsidian vault's file system
/// and the vector database. It monitors file system events and queues operations to keep the vector database
/// in sync with the vault's state.
/// </para>
/// <para>
/// Key features:
/// - Asynchronous processing of file system events
/// - Queue-based operation handling to prevent parallelization issues
/// - Comprehensive logging for monitoring and debugging
/// - Thread-safe operation queuing and processing
/// </para>
/// </remarks>
public class SyncManager : IDisposable
{
    private readonly ILogger<SyncManager> _logger = LoggerService.GetLogger<SyncManager>();
    private readonly FileSystemWatcher watcher;
    private readonly ObsidianDB DB;
    private readonly HashSet<string> lockedPaths = new();
    private string lockedPath = string.Empty;
    private bool _disposed;
    private readonly Queue<SyncOperation> _syncQueue = new();
    private readonly SemaphoreSlim _queueLock = new SemaphoreSlim(1, 1);
    private Task? _syncTask;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    /// <summary>
    /// Gets or sets whether the SyncManager is actively processing file system events.
    /// </summary>
    /// <value>
    /// <c>true</c> if the SyncManager is active; otherwise, <c>false</c>.
    /// </value>
    public bool Active = false;

    /// <summary>
    /// Represents the type of synchronization operation to be performed.
    /// </summary>
    private enum SyncOperationType
    {
        /// <summary>
        /// Operation to index a new note in the vector database.
        /// </summary>
        Index,

        /// <summary>
        /// Operation to delete a note from the vector database.
        /// </summary>
        Delete,

        /// <summary>
        /// Operation to update an existing note in the vector database.
        /// </summary>
        Update
    }

    /// <summary>
    /// Represents a single synchronization operation to be processed.
    /// </summary>
    private class SyncOperation
    {
        /// <summary>
        /// Gets the type of synchronization operation.
        /// </summary>
        public SyncOperationType Type { get; }

        /// <summary>
        /// Gets the path of the file associated with the operation.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Gets the content of the file, if available.
        /// </summary>
        public string? Content { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncOperation"/> class.
        /// </summary>
        /// <param name="type">The type of synchronization operation.</param>
        /// <param name="path">The path of the file associated with the operation.</param>
        /// <param name="content">Optional content of the file.</param>
        public SyncOperation(SyncOperationType type, string path, string? content = null)
        {
            Type = type;
            Path = path;
            Content = content;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncManager"/> class.
    /// </summary>
    /// <param name="db">The ObsidianDB instance to synchronize with.</param>
    /// <exception cref="ArgumentNullException">Thrown when db is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the vault path is invalid.</exception>
    /// <remarks>
    /// Sets up a FileSystemWatcher to monitor the Obsidian vault directory
    /// for changes to markdown files. The watcher is configured to track
    /// various file system events including changes, creations, deletions,
    /// and renames.
    /// </remarks>
    public SyncManager(ObsidianDB db)
    {
        DB = db ?? throw new ArgumentNullException(nameof(db));
        
        if (string.IsNullOrWhiteSpace(db.VaultPath))
        {
            throw new ArgumentException("Vault path cannot be null or empty", nameof(db));
        }

        if (!Directory.Exists(db.VaultPath))
        {
            throw new ArgumentException($"Vault path does not exist: {db.VaultPath}", nameof(db));
        }

        watcher = new FileSystemWatcher(db.VaultPath)
        {
            NotifyFilter = NotifyFilters.Attributes
                         | NotifyFilters.CreationTime
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.FileName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size,
            Filter = "*.md",
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        watcher.Changed += OnChanged;
        watcher.Created += OnCreated;
        watcher.Deleted += OnDeleted;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;

        _logger.LogInformation("SyncManager initialized for vault: {VaultPath}", db.VaultPath);
        Active = true;
    }

    /// <summary>
    /// Performs periodic synchronization tasks.
    /// </summary>
    /// <remarks>
    /// This method is called periodically to handle any pending
    /// synchronization operations. It starts the sync task if it's not
    /// already running.
    /// </remarks>
    public void Tick()
    {
        if (!Active) return;

        // Start the sync task if it's not running
        if (_syncTask == null || _syncTask.IsCompleted)
        {
            _syncTask = ProcessSyncQueueAsync();
        }
    }

    /// <summary>
    /// Processes the synchronization queue asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// This method continuously processes operations from the queue until
    /// cancellation is requested. It handles each operation type appropriately
    /// and logs the progress of operations.
    /// </remarks>
    private async Task ProcessSyncQueueAsync()
    {
        _logger.LogInformation("Starting sync queue processing");
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Waiting for queue lock");
                await _queueLock.WaitAsync(_cancellationTokenSource.Token);
                if (_syncQueue.Count == 0)
                {
                    _logger.LogDebug("Queue is empty, waiting for new operations");
                    _queueLock.Release();
                    await Task.Delay(100, _cancellationTokenSource.Token);
                    continue;
                }

                var operation = _syncQueue.Dequeue();
                _logger.LogDebug("Dequeued operation: {Type} for {Path}", operation.Type, operation.Path);
                _queueLock.Release();

                switch (operation.Type)
                {
                    case SyncOperationType.Index:
                        _logger.LogInformation("Processing index operation for {Path}", operation.Path);
                        await IndexNoteAsync(operation.Path, operation.Content);
                        break;
                    case SyncOperationType.Delete:
                        _logger.LogInformation("Processing delete operation for {Path}", operation.Path);
                        await DeleteNoteAsync(operation.Path);
                        break;
                    case SyncOperationType.Update:
                        _logger.LogInformation("Processing update operation for {Path}", operation.Path);
                        await UpdateNoteAsync(operation.Path, operation.Content);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Sync queue processing cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing sync queue");
                // Continue processing other items in the queue
            }
        }
        _logger.LogInformation("Sync queue processing stopped");
    }

    /// <summary>
    /// Indexes a note in the vector database asynchronously.
    /// </summary>
    /// <param name="path">The path of the note to index.</param>
    /// <param name="content">Optional content of the note. If null, the content will be read from the file.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// This method reads the note content if not provided, and indexes it in the vector database
    /// using the default index. It handles errors gracefully and logs the operation status.
    /// </remarks>
    private async Task IndexNoteAsync(string path, string? content)
    {
        try
        {
            _logger.LogDebug("Starting index operation for {Path}", path);
            if (content == null)
            {
                _logger.LogDebug("Reading content from file: {Path}", path);
                content = await File.ReadAllTextAsync(path);
            }

            if (DB._vectorDB != null)
            {
                _logger.LogDebug("Indexing content in vector database");
                DB._vectorDB.IndexDocument(content, null, null, "Default");
                _logger.LogInformation("Successfully indexed note: {Path}", path);
            }
            else
            {
                _logger.LogWarning("Vector database is null, skipping indexing for {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index note: {Path}", path);
        }
    }

    /// <summary>
    /// Deletes a note from the vector database asynchronously.
    /// </summary>
    /// <param name="path">The path of the note to delete.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// This method finds the note by its path, gets its ID, and uses the Purge method
    /// to remove all vector entries associated with that note ID. It handles errors
    /// gracefully and logs the operation status.
    /// </remarks>
    private async Task DeleteNoteAsync(string path)
    {
        try
        {
            _logger.LogDebug("Starting delete operation for {Path}", path);
            if (DB._vectorDB != null)
            {
                // Get the note to get its ID
                var note = DB.GetFromPath(path);
                if (note != null)
                {
                    _logger.LogDebug("Found note with ID: {NoteId}", note.ID);
                    // Use Purge to remove all entries with this note's ID
                    int removed = DB._vectorDB.Purge(doc => doc.StartsWith($"{note.ID}|"));
                    _logger.LogInformation("Removed {Count} entries from vector database for note: {Path}", removed, path);
                }
                else
                {
                    _logger.LogWarning("Note not found in database: {Path}", path);
                }
            }
            else
            {
                _logger.LogWarning("Vector database is null, skipping deletion for {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete note from vector database: {Path}", path);
        }
    }

    /// <summary>
    /// Updates a note in the vector database asynchronously.
    /// </summary>
    /// <param name="path">The path of the note to update.</param>
    /// <param name="content">Optional content of the note. If null, the content will be read from the file.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// This method performs an update by first deleting the old version of the note
    /// and then indexing the new version. It handles errors gracefully and logs the
    /// operation status.
    /// </remarks>
    private async Task UpdateNoteAsync(string path, string? content)
    {
        try
        {
            _logger.LogDebug("Starting update operation for {Path}", path);
            if (content == null)
            {
                _logger.LogDebug("Reading content from file: {Path}", path);
                content = await File.ReadAllTextAsync(path);
            }

            // First delete the old version
            _logger.LogDebug("Deleting old version of note: {Path}", path);
            await DeleteNoteAsync(path);
            // Then index the new version
            _logger.LogDebug("Indexing new version of note: {Path}", path);
            await IndexNoteAsync(path, content);
            _logger.LogInformation("Successfully updated note: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update note in vector database: {Path}", path);
        }
    }

    /// <summary>
    /// Enqueues a synchronization operation.
    /// </summary>
    /// <param name="operation">The operation to enqueue.</param>
    /// <remarks>
    /// This method adds an operation to the synchronization queue in a thread-safe manner.
    /// It logs the operation details and the current queue size.
    /// </remarks>
    private void EnqueueSyncOperation(SyncOperation operation)
    {
        _logger.LogDebug("Attempting to enqueue operation: {Type} for {Path}", operation.Type, operation.Path);
        _queueLock.Wait();
        try
        {
            _syncQueue.Enqueue(operation);
            _logger.LogDebug("Successfully enqueued operation: {Type} for {Path}. Queue size: {Size}", 
                operation.Type, operation.Path, _syncQueue.Count);
        }
        finally
        {
            _queueLock.Release();
        }
    }

    /// <summary>
    /// Handles file change events from the FileSystemWatcher.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The event data containing information about the file change.</param>
    /// <remarks>
    /// This method is called when a file in the vault is modified.
    /// It checks if the file is locked and if the change type is
    /// appropriate before enqueueing an update operation.
    /// </remarks>
    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if(!Active){return;}
        if (e == null)
        {
            _logger.LogWarning("Received null FileSystemEventArgs in OnChanged");
            return;
        }

        if (lockedPaths.Contains(e.FullPath))
        {
            _logger.LogDebug("Ignoring change to locked file: {Path}", e.FullPath);
            return;
        }

        try
        {
            if (e.ChangeType != WatcherChangeTypes.Changed)
            {
                _logger.LogDebug("Ignoring non-change event: {ChangeType} for {Path}", e.ChangeType, e.FullPath);
                return;
            }

            if (e.FullPath == lockedPath)
            {
                _logger.LogDebug("Ignoring change to currently locked path: {Path}", e.FullPath);
                return;
            }

            _logger.LogInformation("File changed: {Path}", e.FullPath);
            EnqueueSyncOperation(new SyncOperation(SyncOperationType.Update, e.FullPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file change for {Path}", e.FullPath);
        }
    }

    /// <summary>
    /// Handles file creation events from the FileSystemWatcher.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The event data containing information about the created file.</param>
    /// <remarks>
    /// This method is called when a new markdown file is created in the vault.
    /// It enqueues an index operation for the new file.
    /// </remarks>
    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if(!Active){return;}
        if (e == null)
        {
            _logger.LogWarning("Received null FileSystemEventArgs in OnCreated");
            return;
        }

        try
        {
            _logger.LogInformation("File created: {Path}", e.FullPath);
            EnqueueSyncOperation(new SyncOperation(SyncOperationType.Index, e.FullPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file creation for {Path}", e.FullPath);
        }
    }

    /// <summary>
    /// Handles file deletion events from the FileSystemWatcher.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The event data containing information about the deleted file.</param>
    /// <remarks>
    /// This method is called when a markdown file is deleted from the vault.
    /// It checks if the deleted file is locked before enqueueing a delete operation.
    /// </remarks>
    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if(!Active){return;}
        if (e == null)
        {
            _logger.LogWarning("Received null FileSystemEventArgs in OnDeleted");
            return;
        }

        if (lockedPaths.Contains(e.FullPath))
        {
            _logger.LogDebug("Ignoring deletion of locked file: {Path}", e.FullPath);
            return;
        }

        try
        {
            _logger.LogInformation("File deleted: {Path}", e.FullPath);
            EnqueueSyncOperation(new SyncOperation(SyncOperationType.Delete, e.FullPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file deletion for {Path}", e.FullPath);
        }
    }

    /// <summary>
    /// Handles file rename events from the FileSystemWatcher.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The event data containing information about the renamed file.</param>
    /// <remarks>
    /// This method is called when a markdown file is renamed in the vault.
    /// It checks if the renamed file is locked before enqueueing delete and index
    /// operations for the old and new paths respectively.
    /// </remarks>
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if(!Active){return;}
        if (e == null)
        {
            _logger.LogWarning("Received null RenamedEventArgs in OnRenamed");
            return;
        }

        if (lockedPaths.Contains(e.OldFullPath))
        {
            _logger.LogDebug("Ignoring rename of locked file: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
            return;
        }

        try
        {
            _logger.LogInformation("File renamed: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
            // For rename operations, we need to delete the old path and index the new one
            EnqueueSyncOperation(new SyncOperation(SyncOperationType.Delete, e.OldFullPath));
            EnqueueSyncOperation(new SyncOperation(SyncOperationType.Index, e.FullPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file rename from {OldPath} to {NewPath}", e.OldFullPath, e.FullPath);
        }
    }

    /// <summary>
    /// Handles error events from the FileSystemWatcher.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The event data containing information about the error.</param>
    /// <remarks>
    /// This method logs any errors that occur in the FileSystemWatcher
    /// using the configured logger.
    /// </remarks>
    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "File system watcher error occurred");
    }

    /// <summary>
    /// Releases all resources used by the SyncManager instance.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the SyncManager instance and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    /// <remarks>
    /// This method ensures proper cleanup of all resources, including:
    /// - Cancelling the sync task
    /// - Waiting for the task to complete
    /// - Disposing of the cancellation token and semaphore
    /// - Disposing of the file system watcher
    /// </remarks>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _logger.LogInformation("Disposing SyncManager");
            _logger.LogDebug("Cancelling sync task");
            _cancellationTokenSource.Cancel();
            _logger.LogDebug("Waiting for sync task to complete");
            _syncTask?.Wait();
            _logger.LogDebug("Disposing resources");
            _cancellationTokenSource.Dispose();
            _queueLock.Dispose();
            watcher?.Dispose();
            _logger.LogInformation("SyncManager disposed successfully");
        }

        _disposed = true;
    }

    /// <summary>
    /// Finalizes the SyncManager instance.
    /// </summary>
    ~SyncManager()
    {
        Dispose(false);
    }
}
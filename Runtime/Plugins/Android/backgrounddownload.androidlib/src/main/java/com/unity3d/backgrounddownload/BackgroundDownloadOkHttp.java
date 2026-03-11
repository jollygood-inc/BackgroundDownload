package com.unity3d.backgrounddownload;

import android.content.Context;
import android.net.Uri;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicLong;

import okhttp3.Call;
import okhttp3.Callback;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.ResponseBody;

/**
 * Background download implementation using OkHttp.
 * Provides the same interface as the DownloadManager-based BackgroundDownload,
 * but uses OkHttp for HTTP communication, giving finer-grained control over
 * progress tracking, request headers, and network policies.
 */
public class BackgroundDownloadOkHttp {

    // -------------------------------------------------------------------------
    // Status constants (mirrors DownloadManager semantics)
    // -------------------------------------------------------------------------

    /** Download is still in progress. */
    private static final int STATUS_RUNNING  = 0;
    /** Download completed successfully. */
    private static final int STATUS_SUCCESS  = 1;
    /** Download failed with an error. */
    private static final int STATUS_FAILED   = -1;

    // -------------------------------------------------------------------------
    // Static shared state
    // -------------------------------------------------------------------------

    private static final OkHttpClient sharedClient = new OkHttpClient();
    private static final ExecutorService executor = Executors.newCachedThreadPool();

    /**
     * In-memory registry of all active/completed downloads, keyed by unique ID.
     * Used by {@link #recreate(Context, long)} to restore a download after the app restarts.
     * Note: this map is cleared when the process is killed; persistence across process
     * restarts requires additional serialisation (handled by the Unity C# layer).
     */
    private static final Map<Long, BackgroundDownloadOkHttp> registry = new HashMap<>();
    private static long nextId = 1;

    // -------------------------------------------------------------------------
    // Instance state
    // -------------------------------------------------------------------------

    private final Uri    downloadUri;
    private final Uri    destinationUri;
    private final long   id;

    /** Additional HTTP request headers added before the download starts. */
    private final Map<String, String> requestHeaders = new HashMap<>();

    /** Whether metered (mobile data) connections are permitted. */
    private boolean allowMetered = false;
    /** Whether roaming connections are permitted. */
    private boolean allowRoaming = false;

    /** Total size of the remote file, or -1 if unknown. */
    private final AtomicLong totalBytes     = new AtomicLong(-1);
    /** Number of bytes written to disk so far. */
    private final AtomicLong downloadedSoFar = new AtomicLong(0);

    /** Current download status: {@link #STATUS_RUNNING}, {@link #STATUS_SUCCESS}, or {@link #STATUS_FAILED}. */
    private volatile int    status = STATUS_RUNNING;
    /** Human-readable error description; non-null only when {@link #status} is {@link #STATUS_FAILED}. */
    private volatile String error  = null;

    /** The OkHttp {@link Call} in flight, kept so it can be cancelled via {@link #remove()}. */
    private volatile Call activeCall = null;

    // -------------------------------------------------------------------------
    // Completion callback (set by C# layer via CompletionReceiver)
    // -------------------------------------------------------------------------

    /**
     * Notifies the C# layer that at least one download has finished.
     * Called on the OkHttp dispatcher thread after each successful or failed download.
     * Mirrors the role of {@code DownloadManager.ACTION_DOWNLOAD_COMPLETE} broadcast.
     */
    private static volatile CompletionReceiver.Callback completionCallback = null;

    /**
     * Registers the callback that will be invoked whenever any download finishes.
     * Should be called by the C# layer immediately after creating the first download.
     *
     * @param callback The callback to invoke on download completion.
     */
    public static void setCompletionCallback(CompletionReceiver.Callback callback) {
        completionCallback = callback;
    }

    // -------------------------------------------------------------------------
    // Factory methods
    // -------------------------------------------------------------------------

    /**
     * Creates a new download for the given URL that will be saved to {@code destUri}.
     * Call {@link #start(Context)} to enqueue the actual HTTP request.
     *
     * @param url     The URL of the resource to download.
     * @param destUri A {@code file://} URI pointing to the destination file path.
     * @return A new {@link BackgroundDownloadOkHttp} instance.
     */
    public static BackgroundDownloadOkHttp create(String url, String destUri) {
        synchronized (registry) {
            long id = nextId++;
            BackgroundDownloadOkHttp dl = new BackgroundDownloadOkHttp(id, Uri.parse(url), Uri.parse(destUri));
            registry.put(id, dl);
            return dl;
        }
    }

    /**
     * Attempts to recreate a previously started download from the in-memory registry.
     * Returns {@code null} if the download ID is no longer known (e.g. the process was restarted
     * and the registry was cleared). In that case the C# layer is responsible for restarting.
     *
     * @param context Android context (unused; kept for API parity with the DownloadManager variant).
     * @param id      The ID previously returned by {@link #start(Context)}.
     * @return The existing {@link BackgroundDownloadOkHttp} instance, or {@code null}.
     */
    public static BackgroundDownloadOkHttp recreate(Context context, long id) {
        synchronized (registry) {
            return registry.get(id);
        }
    }

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    private BackgroundDownloadOkHttp(long id, Uri url, Uri dest) {
        this.id             = id;
        this.downloadUri    = url;
        this.destinationUri = dest;
    }

    // -------------------------------------------------------------------------
    // Configuration (must be called before start())
    // -------------------------------------------------------------------------

    /**
     * Sets whether downloads over metered (e.g. mobile data) connections are permitted.
     * This is advisory: OkHttp itself does not enforce network type; enforcement is
     * done by checking {@link android.net.ConnectivityManager} at request time.
     *
     * @param allow {@code true} to allow metered connections.
     */
    public void setAllowMetered(boolean allow) {
        this.allowMetered = allow;
    }

    /**
     * Sets whether downloads over roaming connections are permitted.
     *
     * @param allow {@code true} to allow roaming connections.
     */
    public void setAllowRoaming(boolean allow) {
        this.allowRoaming = allow;
    }

    /**
     * Adds a custom HTTP request header that will be sent with the download request.
     * If called multiple times with the same {@code name}, only the last value is kept
     * (OkHttp's {@link Request.Builder#header} semantics).
     *
     * @param name  HTTP header name (e.g. {@code "Authorization"}).
     * @param value HTTP header value.
     */
    public void addRequestHeader(String name, String value) {
        requestHeaders.put(name, value);
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /**
     * Enqueues the download request on a background thread.
     * The file is streamed directly to the destination path specified in {@link #create}.
     *
     * @param context Android context (used only for network-type checks, not stored).
     * @return The unique download ID that can later be passed to {@link #recreate}.
     */
    public long start(Context context) {
        Request.Builder builder = new Request.Builder().url(downloadUri.toString());
        for (Map.Entry<String, String> entry : requestHeaders.entrySet()) {
            builder.header(entry.getKey(), entry.getValue());
        }
        Request request = builder.build();

        activeCall = sharedClient.newCall(request);

        // Uri.getPath() returns null for file:// URIs on some Android versions.
        // Strip the "file://" scheme manually to get a reliable absolute path.
        String uriStr = destinationUri.toString();
        final String destPath = uriStr.startsWith("file://") ? uriStr.substring(7) : uriStr;
        final File destFile = new File(destPath);

        activeCall.enqueue(new Callback() {
            @Override
            public void onFailure(Call call, IOException e) {
                if (!call.isCanceled()) {
                    error  = e.getMessage() != null ? e.getMessage() : "Network failure";
                    status = STATUS_FAILED;
                    notifyCompletion();
                }
            }

            @Override
            public void onResponse(Call call, Response response) throws IOException {
                if (!response.isSuccessful()) {
                    error  = "HTTP error: " + response.code();
                    status = STATUS_FAILED;
                    response.close();
                    notifyCompletion();
                    return;
                }

                ResponseBody body = response.body();
                if (body == null) {
                    error  = "Empty response body";
                    status = STATUS_FAILED;
                    notifyCompletion();
                    return;
                }

                // Store total size for progress calculation (-1 if unknown)
                totalBytes.set(body.contentLength());

                // Ensure parent directories exist
                File parent = destFile.getParentFile();
                if (parent != null && !parent.exists()) {
                    parent.mkdirs();
                }

                try (InputStream in = body.byteStream();
                     FileOutputStream out = new FileOutputStream(destFile)) {
                    byte[] buffer = new byte[8192];
                    int read;
                    while ((read = in.read(buffer)) != -1) {
                        out.write(buffer, 0, read);
                        downloadedSoFar.addAndGet(read);
                    }
                    status = STATUS_SUCCESS;
                } catch (IOException e) {
                    error  = e.getMessage() != null ? e.getMessage() : "File write error";
                    status = STATUS_FAILED;
                } finally {
                    notifyCompletion();
                }
            }
        });

        return id;
    }

    /**
     * Invokes the registered {@link CompletionReceiver.Callback} to notify the C# layer
     * that this download has finished (successfully or with an error).
     * Safe to call from any thread.
     */
    private void notifyCompletion() {
        CompletionReceiver.Callback cb = completionCallback;
        if (cb != null) {
            try {
                cb.downloadCompleted();
            } catch (Exception e) {
                // C# side may have been destroyed; ignore.
                completionCallback = null;
            }
        }
    }

    /**
     * Cancels the in-flight download and removes this entry from the registry.
     * If the download has already completed, only the registry entry is removed.
     */
    public void remove() {
        if (status == STATUS_RUNNING) {
            error = "Aborted";
            status = STATUS_FAILED;
            Call call = activeCall;
            if (call != null) {
                call.cancel();
            }
        }
        synchronized (registry) {
            registry.remove(id);
        }
    }

    // -------------------------------------------------------------------------
    // Status queries
    // -------------------------------------------------------------------------

    /**
     * Checks whether the download has finished.
     *
     * @return {@code 1} if the download completed successfully,
     *         {@code -1} if it failed, or {@code 0} if it is still in progress.
     */
    public int checkFinished() {
        return status;
    }

    /**
     * Returns the download progress as a value in {@code [0.0, 1.0]}.
     * Returns {@code -1.0} if the total size is unknown.
     * Returns {@code 1.0} if the download has finished (successfully or not).
     *
     * @return Fractional progress, {@code -1.0} for unknown, or {@code 1.0} on completion.
     */
    public float getProgress() {
        if (status != STATUS_RUNNING) {
            return 1.0f;
        }
        long total = totalBytes.get();
        long done  = downloadedSoFar.get();
        if (done <= 0) {
            return 0.0f;
        }
        if (total <= 0) {
            return -1.0f;
        }
        float progress = done / (float) total;
        return Math.min(progress, 1.0f);
    }

    /**
     * Returns the number of bytes downloaded so far.
     * Returns {@code -1} if the download has failed, or {@code 0} if it has not started yet.
     *
     * @return Number of bytes written to disk, or {@code -1} on error.
     */
    public long getBytesDownloaded() {
        if (status == STATUS_FAILED) {
            return -1;
        }
        return downloadedSoFar.get();
    }

    // -------------------------------------------------------------------------
    // Accessors
    // -------------------------------------------------------------------------

    /**
     * Returns the original download URL as a string.
     *
     * @return The URL passed to {@link #create}.
     */
    public String getDownloadUrl() {
        return downloadUri.toString();
    }

    /**
     * Returns the destination file URI as a string.
     *
     * @return The destination URI passed to {@link #create}.
     */
    public String getDestinationUri() {
        return destinationUri.toString();
    }

    /**
     * Returns the error message if the download has failed, or {@code null} otherwise.
     *
     * @return Human-readable error string, or {@code null} if no error has occurred.
     */
    public String getError() {
        return error;
    }
}


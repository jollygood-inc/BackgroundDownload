package com.unity3d.backgrounddownload;

import android.content.Context;
import android.net.Uri;
import android.util.Log;

import java.io.File;
import java.io.FileOutputStream;
import java.io.BufferedOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;

import okhttp3.Call;
import okhttp3.Callback;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.ResponseBody;
import okhttp3.ConnectionPool;

/**
 * Background download implementation using OkHttp.
 * Provides the same interface as the DownloadManager-based BackgroundDownload,
 * but uses OkHttp for HTTP communication, giving finer-grained control over
 * progress tracking, request headers, and network policies.
 */
public class BackgroundDownloadOkHttp {

    private static final String TAG = "BackgroundDownloadOkHttpJava";

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

    private static final OkHttpClient client = new OkHttpClient.Builder()
                                 .retryOnConnectionFailure(true)
                                 .connectionPool(new ConnectionPool(20, 10, TimeUnit.MINUTES))
                                 .connectTimeout(30, TimeUnit.SECONDS)
                                 .readTimeout(30, TimeUnit.SECONDS)
                                 .writeTimeout(30, TimeUnit.SECONDS)
                                 .build();

    private static final Map<Long, BackgroundDownloadOkHttp> registry = new HashMap<>();
    private static long nextId = 1;

    // -------------------------------------------------------------------------
    // Instance state
    // -------------------------------------------------------------------------

    private final Uri    downloadUri;
    private final Uri    destinationUri;
    private final long   id;

    private final Map<String, String> requestHeaders = new HashMap<>();

    private boolean allowMetered = false;
    private boolean allowRoaming = false;

    private final AtomicLong totalBytes      = new AtomicLong(-1);
    private final AtomicLong downloadedSoFar = new AtomicLong(0);

    private volatile int    status = STATUS_RUNNING;
    private volatile String error  = null;

    private volatile Call activeCall = null;

    // -------------------------------------------------------------------------
    // Completion callback
    // -------------------------------------------------------------------------

    private static volatile CompletionReceiver.Callback completionCallback = null;

    public static void setCompletionCallback(CompletionReceiver.Callback callback) {
        completionCallback = callback;
    }

    // -------------------------------------------------------------------------
    // Factory methods
    // -------------------------------------------------------------------------

    public static BackgroundDownloadOkHttp create(String url, String destUri) {
        synchronized (registry) {
            long id = nextId++;
            BackgroundDownloadOkHttp dl = new BackgroundDownloadOkHttp(id, Uri.parse(url), Uri.parse(destUri));
            registry.put(id, dl);
            return dl;
        }
    }

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
    // Configuration
    // -------------------------------------------------------------------------

    public void setAllowMetered(boolean allow) {
        this.allowMetered = allow;
    }

    public void setAllowRoaming(boolean allow) {
        this.allowRoaming = allow;
    }

    public void addRequestHeader(String name, String value) {
        requestHeaders.put(name, value);
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public long start(Context context) {
        downloadedSoFar.set(0);
        
        Request.Builder builder = new Request.Builder().url(downloadUri.toString());
        for (Map.Entry<String, String> entry : requestHeaders.entrySet()) {
            builder.header(entry.getKey(), entry.getValue());
        }
        Request request = builder.build();

        activeCall = client.newCall(request);

        final File destFile;
        try {
            String destPath = destinationUri.getPath();
            if (destPath == null || destPath.isEmpty()) {
                throw new IllegalStateException("Destination path is null/empty. destinationUri=" + destinationUri);
            }
            destFile = new File(destPath);
        } catch (Exception e) {
            error  = "Invalid destination uri: " + e.getMessage();
            status = STATUS_FAILED;
            Log.e(TAG, "start failed before enqueue. id=" + id + ", destinationUri=" + destinationUri, e);
            notifyCompletion();
            return id;
        }

        activeCall.enqueue(new Callback() {
            @Override
            public void onFailure(Call call, IOException e) {
                if (!call.isCanceled()) {
                    error  = e.getMessage() != null ? e.getMessage() : "Network failure";
                    status = STATUS_FAILED;
                    Log.e(TAG, "download onFailure. id=" + id + ", error=" + error, e);
                    notifyCompletion();
                }
            }

            @Override
            public void onResponse(Call call, Response response) throws IOException {
                if (!response.isSuccessful()) {
                    error  = "HTTP error: " + response.code();
                    status = STATUS_FAILED;
                    Log.e(TAG, "download HTTP fail. id=" + id + ", code=" + response.code());
                    response.close();
                    notifyCompletion();
                    return;
                }

                boolean writeSuccess = false;
                long total = -1;
                try (ResponseBody body = response.body()) {
                    if (body == null) {
                        error  = "Empty response body";
                        status = STATUS_FAILED;
                        Log.e(TAG, "download body null. id=" + id);
                        notifyCompletion();
                        return;
                    }

                    total = body.contentLength();
                    totalBytes.set(total);

                    File parent = destFile.getParentFile();
                    if (parent != null && !parent.exists() && !parent.mkdirs() && !parent.exists()) {
                        error  = "Failed to create parent directory: " + parent.getAbsolutePath();
                        status = STATUS_FAILED;
                        Log.e(TAG, "mkdirs failed. id=" + id + ", dir=" + parent.getAbsolutePath());
                        notifyCompletion();
                        return;
                    }

                try (InputStream in = body.byteStream();
                         FileOutputStream fos = new FileOutputStream(destFile);
                         BufferedOutputStream out = new BufferedOutputStream(fos, 131072)) {
                    
                        byte[] buffer = new byte[131072];
                        int read;
                    
                        while ((read = in.read(buffer)) != -1) {
                            out.write(buffer, 0, read);
                            downloadedSoFar.addAndGet(read);
                        }
                    
                        out.flush();
                        fos.getFD().sync();
                    
                        writeSuccess = true;
                    }
                } catch (IOException e) {
                    error  = e.getMessage() != null ? e.getMessage() : "File write error";
                    status = STATUS_FAILED;
                    Log.e(TAG, "file write failed. id=" + id + ", path=" + destFile.getAbsolutePath(), e);
                }

                if (writeSuccess) {
                    // If content length is known, ensure we wrote the full payload.
                        long written = downloadedSoFar.get();
                        
                        if (total > 0) {
                            if (written != total) {
                                error = "Downloaded size mismatch: expected " + total + " got " + written;
                                status = STATUS_FAILED;
                                destFile.delete();
                            } else {
                                status = STATUS_SUCCESS;
                            }
                        } else {
                            if (written <= 0) {
                                error = "Downloaded zero bytes";
                                status = STATUS_FAILED;
                                destFile.delete();
                            } else {
                                status = STATUS_SUCCESS;
                            }
                        }
                } else {
                    // Clean up partial file on failure.
                    //noinspection ResultOfMethodCallIgnored
                    destFile.delete();
                }

                notifyCompletion();
            }
        });

        return id;
    }

    private void notifyCompletion() {
        CompletionReceiver.Callback cb = completionCallback;
        if (cb != null) {
            try {
                cb.downloadCompleted();
            } catch (Exception e) {
                Log.e(TAG, "Error while executing completion callback", e);
            }
        }
    }

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

    public int checkFinished() {
        return status;
    }

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

    public long getBytesDownloaded() {
        if (status == STATUS_FAILED) {
            return -1;
        }
        return downloadedSoFar.get();
    }

    // -------------------------------------------------------------------------
    // Accessors
    // -------------------------------------------------------------------------

    public String getDownloadUrl() {
        return downloadUri.toString();
    }

    public String getDestinationUri() {
        return destinationUri.toString();
    }

    public String getError() {
        return error;
    }
}


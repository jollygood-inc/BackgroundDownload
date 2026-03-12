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
 * OkHttp を使用したバックグラウンドダウンロードの実装。
 * DownloadManager ベースの BackgroundDownload と同じインターフェースを提供しますが、
 * OkHttp を HTTP 通信に使用することで、進捗追跡やリクエストヘッダーの制御が可能です。
 */
public class BackgroundDownloadOkHttp {

    private static final String TAG = "BackgroundDownloadOkHttpJava";

    // -------------------------------------------------------------------------
    // ステータス定数（DownloadManager のセマンティクスに準拠）
    // -------------------------------------------------------------------------

    /** ダウンロードが進行中 */
    private static final int STATUS_RUNNING = 0;
    /** ダウンロードが正常完了 */
    private static final int STATUS_SUCCESS = 1;
    /** ダウンロードが失敗 */
    private static final int STATUS_FAILED  = -1;

    // -------------------------------------------------------------------------
    // 静的共有状態
    // -------------------------------------------------------------------------

    /** 全ダウンロードで共有する OkHttpClient */
    private static final OkHttpClient client = new OkHttpClient.Builder()
            .retryOnConnectionFailure(true)
            .connectionPool(new ConnectionPool(20, 10, TimeUnit.MINUTES))
            .connectTimeout(30, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .writeTimeout(30, TimeUnit.SECONDS)
            .build();

    /**
     * アクティブ／完了済みのダウンロードを ID をキーとして保持するインメモリレジストリ。
     * プロセスが再起動されるとこのマップは失われます。
     */
    private static final Map<Long, BackgroundDownloadOkHttp> registry = new HashMap<>();

    /** 次に発行するダウンロード ID */
    private static long nextId = 1;

    // -------------------------------------------------------------------------
    // インスタンス状態
    // -------------------------------------------------------------------------

    /** ダウンロード元 URL */
    private final Uri downloadUri;
    /** ダウンロード先ファイル URI */
    private final Uri destinationUri;
    /** このダウンロードの一意 ID */
    private final long id;

    /** ダウンロード開始前に追加する HTTP リクエストヘッダー */
    private final Map<String, String> requestHeaders = new HashMap<>();

    /** モバイルデータ接続を許可するか */
    private boolean allowMetered = false;
    /** ローミング接続を許可するか */
    private boolean allowRoaming = false;

    /** リモートファイルの合計バイト数（不明な場合は -1） */
    private final AtomicLong totalBytes = new AtomicLong(-1);
    /** これまでにディスクへ書き込んだバイト数 */
    private final AtomicLong downloadedSoFar = new AtomicLong(0);

    /** 現在のダウンロードステータス */
    private volatile int status = STATUS_RUNNING;
    /** エラー内容（失敗時のみ非 null） */
    private volatile String error = null;

    /** キャンセル用に保持する進行中の OkHttp Call */
    private volatile Call activeCall = null;

    // -------------------------------------------------------------------------
    // 完了コールバック
    // -------------------------------------------------------------------------

    /**
     * ダウンロード完了を C# レイヤーへ通知するコールバック。
     * OkHttp のディスパッチャスレッドから呼び出されます。
     */
    private static volatile CompletionReceiver.Callback completionCallback = null;

    /**
     * ダウンロード完了時に呼び出すコールバックを登録します。
     * 最初のダウンロード作成直後に C# レイヤーから呼び出してください。
     *
     * @param callback 完了時に呼び出すコールバック
     */
    public static void setCompletionCallback(CompletionReceiver.Callback callback) {
        completionCallback = callback;
    }

    // -------------------------------------------------------------------------
    // ファクトリメソッド
    // -------------------------------------------------------------------------

    /**
     * 指定 URL のリソースを destUri へ保存する新しいダウンロードを作成します。
     * 実際の HTTP リクエストは {@link #start(Context)} で開始されます。
     *
     * @param url     ダウンロードするリソースの URL
     * @param destUri 保存先ファイルを示す {@code file://} URI
     * @return 新しい {@link BackgroundDownloadOkHttp} インスタンス
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
     * 実行中のプロセス内で以前に開始したダウンロードを ID で再取得します。
     *
     * <p><b>注意:</b> registry はインメモリの {@link HashMap} であるため、
     * プロセスが再起動されると内容は失われます。
     * その場合、このメソッドは常に {@code null} を返します。
     * DownloadManager ベースの実装とは異なり、OkHttp 版は
     * プロセスをまたいだダウンロードの復元をサポートしていません。
     * C# 側の LoadDownloads/SaveDownloads は互換性のために存在しますが、
     * プロセス再起動後の復元には機能しません。</p>
     *
     * @param context Android コンテキスト（未使用、API 互換のために保持）
     * @param id      {@link #start(Context)} が返したダウンロード ID
     * @return 同一プロセス内であれば対応するインスタンス、プロセス再起動後は {@code null}
     */
    public static BackgroundDownloadOkHttp recreate(Context context, long id) {
        synchronized (registry) {
            return registry.get(id);
        }
    }

    // -------------------------------------------------------------------------
    // コンストラクタ
    // -------------------------------------------------------------------------

    private BackgroundDownloadOkHttp(long id, Uri url, Uri dest) {
        this.id             = id;
        this.downloadUri    = url;
        this.destinationUri = dest;
    }

    // -------------------------------------------------------------------------
    // 設定（start() 呼び出し前に行うこと）
    // -------------------------------------------------------------------------

    /**
     * モバイルデータ接続でのダウンロードを許可するか設定します。
     *
     * @param allow {@code true} でモバイルデータ接続を許可
     */
    public void setAllowMetered(boolean allow) {
        this.allowMetered = allow;
    }

    /**
     * ローミング接続でのダウンロードを許可するか設定します。
     *
     * @param allow {@code true} でローミング接続を許可
     */
    public void setAllowRoaming(boolean allow) {
        this.allowRoaming = allow;
    }

    /**
     * ダウンロードリクエストに付与するカスタム HTTP ヘッダーを追加します。
     * 同じ名前で複数回呼び出した場合、最後の値が使われます。
     *
     * @param name  HTTP ヘッダー名（例: {@code "Authorization"}）
     * @param value HTTP ヘッダー値
     */
    public void addRequestHeader(String name, String value) {
        requestHeaders.put(name, value);
    }

    // -------------------------------------------------------------------------
    // ライフサイクル
    // -------------------------------------------------------------------------

    /**
     * バックグラウンドスレッドでダウンロードリクエストをエンキューします。
     * ファイルは {@link #create} で指定した保存先パスへ直接ストリーミングされます。
     *
     * @param context Android コンテキスト（ネットワーク種別チェックに使用、保持しない）
     * @return 後で {@link #recreate} に渡せる一意のダウンロード ID
     */
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
            // Uri.getPath() は一部の Android バージョンで file:// URI に対し null を返すことがある。
            // destinationUri.getPath() を使って確実に絶対パスを取得する。
            String destPath = destinationUri.getPath();
            if (destPath == null || destPath.isEmpty()) {
                throw new IllegalStateException("保存先パスが null または空です。 destinationUri=" + destinationUri);
            }
            destFile = new File(destPath);
        } catch (Exception e) {
            error  = "無効な保存先 URI: " + e.getMessage();
            status = STATUS_FAILED;
            Log.e(TAG, "enqueue前にstart失敗。 id=" + id + ", destinationUri=" + destinationUri, e);
            notifyCompletion();
            return id;
        }

        activeCall.enqueue(new Callback() {

            /** ネットワーク障害時のコールバック */
            @Override
            public void onFailure(Call call, IOException e) {
                if (!call.isCanceled()) {
                    error  = e.getMessage() != null ? e.getMessage() : "ネットワーク障害";
                    status = STATUS_FAILED;
                    Log.e(TAG, "ダウンロード失敗。 id=" + id + ", error=" + error, e);
                    notifyCompletion();
                }
            }

            /** HTTPレスポンス受信時のコールバック */
            @Override
            public void onResponse(Call call, Response response) throws IOException {
                if (!response.isSuccessful()) {
                    error  = "HTTP エラー: " + response.code();
                    status = STATUS_FAILED;
                    Log.e(TAG, "HTTP エラー。 id=" + id + ", code=" + response.code());
                    response.close();
                    notifyCompletion();
                    return;
                }

                boolean writeSuccess = false;
                long total = -1;
                try (ResponseBody body = response.body()) {
                    if (body == null) {
                        error  = "レスポンスボディが空です";
                        status = STATUS_FAILED;
                        Log.e(TAG, "ボディが null。 id=" + id);
                        notifyCompletion();
                        return;
                    }

                    // 合計サイズを保存（不明な場合は -1）
                    total = body.contentLength();
                    totalBytes.set(total);

                    // 親ディレクトリが存在しなければ作成する
                    File parent = destFile.getParentFile();
                    if (parent != null && !parent.exists() && !parent.mkdirs() && !parent.exists()) {
                        error  = "親ディレクトリの作成に失敗: " + parent.getAbsolutePath();
                        status = STATUS_FAILED;
                        Log.e(TAG, "mkdirs 失敗。 id=" + id + ", dir=" + parent.getAbsolutePath());
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

                        // ストリームが close される前にディスクへ確実にフラッシュする
                        out.flush();
                        fos.getFD().sync();

                        writeSuccess = true;
                    }
                } catch (IOException e) {
                    error  = e.getMessage() != null ? e.getMessage() : "ファイル書き込みエラー";
                    status = STATUS_FAILED;
                    Log.e(TAG, "ファイル書き込み失敗。 id=" + id + ", path=" + destFile.getAbsolutePath(), e);
                }

                if (writeSuccess) {
                    // コンテンツ長が判明している場合は書き込みバイト数と照合する
                    long written = downloadedSoFar.get();

                    if (total > 0) {
                        if (written != total) {
                            error  = "サイズ不一致: 期待値=" + total + " 実際=" + written;
                            status = STATUS_FAILED;
                            Log.e(TAG, "サイズ不一致。 id=" + id + ", expected=" + total + ", got=" + written);
                            //noinspection ResultOfMethodCallIgnored
                            destFile.delete();
                        } else {
                            status = STATUS_SUCCESS;
                            Log.d(TAG, "ダウンロード成功。 id=" + id + ", path=" + destFile.getAbsolutePath());
                        }
                    } else {
                        if (written <= 0) {
                            error  = "ダウンロードされたバイト数が 0 です";
                            status = STATUS_FAILED;
                            //noinspection ResultOfMethodCallIgnored
                            destFile.delete();
                        } else {
                            status = STATUS_SUCCESS;
                            Log.d(TAG, "ダウンロード成功（サイズ不明）。 id=" + id + ", written=" + written);
                        }
                    }
                } else {
                    // 失敗時は中途半端なファイルを削除する
                    //noinspection ResultOfMethodCallIgnored
                    destFile.delete();
                }

                notifyCompletion();
            }
        });

        return id;
    }

    /**
     * 登録済みの {@link CompletionReceiver.Callback} を呼び出して C# レイヤーへ完了を通知します。
     * 任意のスレッドから安全に呼び出せます。
     */
    private void notifyCompletion() {
        CompletionReceiver.Callback cb = completionCallback;
        if (cb != null) {
            try {
                cb.downloadCompleted();
            } catch (Exception e) {
                // C# 側がすでに破棄されている場合は無視する
                completionCallback = null;
            }
        }
    }

    /**
     * 進行中のダウンロードをキャンセルし、レジストリから削除します。
     * すでに完了している場合はレジストリエントリのみ削除されます。
     */
    public void remove() {
        if (status == STATUS_RUNNING) {
            error  = "中断されました";
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
    // ステータス照会
    // -------------------------------------------------------------------------

    /**
     * ダウンロードが完了しているか確認します。
     *
     * @return 正常完了なら {@code 1}、失敗なら {@code -1}、進行中なら {@code 0}
     */
    public int checkFinished() {
        return status;
    }

    /**
     * ダウンロード進捗を {@code [0.0, 1.0]} で返します。
     * 合計サイズ不明の場合は {@code -1.0}、完了済みの場合は {@code 1.0} を返します。
     *
     * @return 進捗の割合、サイズ不明なら {@code -1.0}、完了なら {@code 1.0}
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
        return Math.min(done / (float) total, 1.0f);
    }

    /**
     * これまでにダウンロードされたバイト数を返します。
     * 失敗時は {@code -1}、未開始時は {@code 0} を返します。
     *
     * @return ディスクへ書き込んだバイト数、またはエラー時 {@code -1}
     */
    public long getBytesDownloaded() {
        if (status == STATUS_FAILED) {
            return -1;
        }
        return downloadedSoFar.get();
    }

    // -------------------------------------------------------------------------
    // アクセサ
    // -------------------------------------------------------------------------

    /**
     * 元のダウンロード URL を文字列で返します。
     *
     * @return {@link #create} に渡した URL
     */
    public String getDownloadUrl() {
        return downloadUri.toString();
    }

    /**
     * 保存先ファイルの URI を文字列で返します。
     *
     * @return {@link #create} に渡した保存先 URI
     */
    public String getDestinationUri() {
        return destinationUri.toString();
    }

    /**
     * ダウンロードが失敗した場合のエラーメッセージを返します。
     * エラーがなければ {@code null} を返します。
     *
     * @return 人間が読めるエラー文字列、またはエラーなしの場合 {@code null}
     */
    public String getError() {
        return error;
    }
}


package com.pulse.videoplayer;

import android.graphics.SurfaceTexture;
import android.content.res.AssetManager;
import android.os.Handler;
import android.os.Looper;
import android.view.Surface;
import java.util.concurrent.atomic.AtomicBoolean;

/** 将系统解码表面与 Unity 渲染线程中的外部纹理连接。 */
public final class PulseSurfaceBridge implements SurfaceTexture.OnFrameAvailableListener {
    private final SurfaceTexture m_surfaceTexture; // 仅创建它的渲染线程可以消费图像。
    private final Surface m_surface; // NDK MediaCodec 的输出端。
    private final AtomicBoolean m_frameAvailable = new AtomicBoolean(); // 跨线程到帧通知。
    private final float[] m_transform = new float[16]; // 重用坐标变换缓冲区。

    /** 加载原生库并注册 JavaVM 与类引用。 */
    public static void Initialize(AssetManager assetManager) {
        System.loadLibrary("PulseVideoNative");
        NativeInitialize(assetManager);
    }

    private static native void NativeInitialize(AssetManager assetManager);

    /** 在 Unity 的 OpenGLES 渲染上下文中创建解码输出表面。 */
    public PulseSurfaceBridge(int texture, int width, int height) {
        m_surfaceTexture = new SurfaceTexture(texture);
        m_surfaceTexture.setDefaultBufferSize(width, height);
        // 回调投递至主 Looper，避免依赖 Unity 渲染线程是否安装 Looper。
        m_surfaceTexture.setOnFrameAvailableListener(this, new Handler(Looper.getMainLooper()));
        m_surface = new Surface(m_surfaceTexture);
    }

    /** 返回供 NDK 转换为 ANativeWindow 的 Surface。 */
    public Surface GetSurface() {
        return m_surface;
    }

    /** 只发布通知，不在 Android 回调线程执行 GL 调用。 */
    @Override
    public void onFrameAvailable(SurfaceTexture surfaceTexture) {
        m_frameAvailable.set(true);
    }

    /** 在渲染线程消费一帧；未到帧时返回空引用。 */
    public float[] UpdateFrame() {
        if (!m_frameAvailable.getAndSet(false)) {
            return null;
        }
        m_surfaceTexture.updateTexImage();
        m_surfaceTexture.getTransformMatrix(m_transform);
        return m_transform;
    }

    /** 在解码线程结束后，于原 GL 上下文释放表面。 */
    public void Release() {
        m_surfaceTexture.setOnFrameAvailableListener(null);
        m_surface.release();
        m_surfaceTexture.release();
    }
}

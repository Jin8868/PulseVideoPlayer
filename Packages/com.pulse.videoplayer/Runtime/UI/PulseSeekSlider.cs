using UnityEngine;
using UnityEngine.EventSystems;

namespace PulseVideoPlayer.UI
{
    /// <summary>由播放器自动添加，转发播放进度滑条的指针和拖动事件。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class PulseSeekSlider : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IBeginDragHandler, IEndDragHandler
    {
        private PulseVideoPlayer m_owner; // 当前拥有此滑条的播放器。

        internal void SetOwner(PulseVideoPlayer owner)
        {
            m_owner = owner;
        }

        /// <summary>按下滑条时开始预览。</summary>
        /// <param name="eventData">当前指针事件。</param>
        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) m_owner?.BeginProgressDrag();
        }

        /// <summary>松开滑条时提交跳转。</summary>
        /// <param name="eventData">当前指针事件。</param>
        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) m_owner?.EndProgressDrag();
        }

        /// <summary>开始拖动时冻结自动进度覆盖。</summary>
        /// <param name="eventData">当前拖动事件。</param>
        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) m_owner?.BeginProgressDrag();
        }

        /// <summary>拖动结束时提交最终位置。</summary>
        /// <param name="eventData">当前拖动事件。</param>
        public void OnEndDrag(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) m_owner?.EndProgressDrag();
        }

        private void OnDisable()
        {
            // 滑条单独禁用时也结束预览，避免进度永久停在拖动状态。
            m_owner?.EndProgressDrag();
        }
    }
}

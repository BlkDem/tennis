using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TennisSim
{
    /// <summary>A menu row with a value: left and right (or a click) step through the choices, up and down move between rows.</summary>
    public sealed class OptionRow : Selectable, ISubmitHandler, IPointerClickHandler
    {
        public Text label;
        public Text value;
        public string[] choices = Array.Empty<string>();
        public int index;
        public event Action<int> Changed;

        public void Show() => value.text = choices.Length > 0 ? $"<   {choices[index]}   >" : "";

        public void Step(int direction)
        {
            if (choices.Length == 0) return;
            index = (index + direction + choices.Length) % choices.Length;
            Show();
            Changed?.Invoke(index);
        }

        public override void OnMove(AxisEventData eventData)
        {
            if (eventData.moveDir == MoveDirection.Left) Step(-1);
            else if (eventData.moveDir == MoveDirection.Right) Step(1);
            else base.OnMove(eventData);
        }

        public void OnSubmit(BaseEventData eventData) => Step(1);
        public void OnPointerClick(PointerEventData eventData) => Step(eventData.position.x < transform.position.x ? -1 : 1);
    }
}

using System.Globalization;
using System.Text;

namespace ServerProbe
{
    // CSV 한 줄을 만든다. 값을 순서대로 Add 하면 쉼표를 알아서 넣는다.
    //
    //   var row = new CsvRow();
    //   row.Add(frame);            // frame
    //   row.Add(rttMs, "0.0");     // est_rtt_ms
    //   ProbeLog.Append(file, header, row.ToString());
    //
    // 숫자는 PC의 지역 설정과 무관하게 항상 같은 모양으로 쓴다(소수점은 '.').
    // Add 하는 순서가 머리 줄의 열 순서와 같아야 한다.
    public sealed class CsvRow
    {
        static readonly CultureInfo s_Invariant = CultureInfo.InvariantCulture;

        readonly StringBuilder m_Text = new StringBuilder(128); // 지금까지 만든 줄

        public void Add(string value)
        {
            AddSeparator();
            m_Text.Append(value);
        }

        public void Add(int value)
        {
            Add(value.ToString(s_Invariant));
        }

        public void Add(uint value)
        {
            Add(value.ToString(s_Invariant));
        }

        public void Add(long value)
        {
            Add(value.ToString(s_Invariant));
        }

        // format 예: "0.0" → 소수 한 자리 고정, "0.##" → 소수 둘째 자리까지(끝의 0은 생략)
        public void Add(float value, string format)
        {
            Add(value.ToString(format, s_Invariant));
        }

        // true → 1, false → 0
        public void Add(bool value)
        {
            Add(value ? "1" : "0");
        }

        public override string ToString()
        {
            return m_Text.ToString();
        }

        // 첫 값이 아니면 앞에 쉼표를 넣는다.
        void AddSeparator()
        {
            if (m_Text.Length > 0)
            {
                m_Text.Append(',');
            }
        }
    }
}

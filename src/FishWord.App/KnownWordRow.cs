using System.ComponentModel;
using System.Text;
using FishWord.Core.Models;

namespace FishWord.App;

/// <summary>熟词列表行：包装 <see cref="Word"/>，提供手风琴展开状态与展示用字符串。</summary>
public sealed class KnownWordRow : INotifyPropertyChanged
{
    private bool _isExpanded;

    public KnownWordRow(Word word) => Word = word;

    public Word Word { get; }

    /// <summary>是否展开详情（手风琴：一次只展开一个）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>详情音标（英 / 美并排）。</summary>
    public string PhoneticDisplay
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Word.PhoneticUk)) parts.Add($"英 /{Word.PhoneticUk}/");
            if (!string.IsNullOrWhiteSpace(Word.PhoneticUs)) parts.Add($"美 /{Word.PhoneticUs}/");
            return string.Join("    ", parts);
        }
    }

    /// <summary>
    /// 详情释义。ECDICT 的 translation（meaning_cn）本身已带词性前缀（如"a. 后面的"），
    /// 而 pos 字段是词性频率统计（如"r:71/v:3/j:2/n:24"），对用户无意义，故不拼入。
    /// </summary>
    public string DefinitionsDisplay
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var d in Word.Definitions)
            {
                if (string.IsNullOrWhiteSpace(d.MeaningCn) && string.IsNullOrWhiteSpace(d.MeaningEn))
                    continue;
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append(!string.IsNullOrWhiteSpace(d.MeaningCn) ? d.MeaningCn : d.MeaningEn);
            }
            return sb.ToString();
        }
    }
}

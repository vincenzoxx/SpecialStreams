using System.ComponentModel;

namespace G20CustomControls
{

	public interface IMediaViewerItem : INotifyPropertyChanged
	{
		MediaViewerTypes MediaViewerType { get; set; }
		string Description { get; set; }
		bool IsPlayable { get; }
	}
}

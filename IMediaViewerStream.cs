using SpecialStreams;

namespace G20CustomControls
{
	public interface IMediaViewerStream : IMediaViewerItem
	{
		MediaStreamReader MediaStreamReader { get; set; }

	}
}

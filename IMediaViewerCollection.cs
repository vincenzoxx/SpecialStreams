using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Itenso.TimePeriod;

namespace G20CustomControls
{
	public interface IMediaViewerCollection : IList<IMediaViewerItem>, INotifyCollectionChanged, IMediaViewerItem
	{
		new string Description { get; set; }
		new MediaViewerTypes MediaViewerType { get; set; }
		new bool IsPlayable { get; }
		DateTime DateTimeStart { get; set; }
		TimePeriodCollection Intervals { get; set; }

		bool HasMapPoints { get; }
	}
}

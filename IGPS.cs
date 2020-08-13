using System;
using System.ComponentModel;

namespace G20CustomControls
{
	public interface IGPS : INotifyPropertyChanged
	{
		float Latitude { get; set; }
		float Longitude { get; set; }
		Decimal Speed { get; set; }
		int Direction { get; set; }
		Decimal DOP { get; set; }
		DateTime DateTime { get; set; }
	}
}

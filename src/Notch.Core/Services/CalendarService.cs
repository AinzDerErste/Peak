using Notch.Core.Models;
using Windows.ApplicationModel.Appointments;

namespace Notch.Core.Services;

public class CalendarService
{
    public event Action<CalendarEvent?>? NextEventChanged;
    public CalendarEvent? NextEvent { get; private set; }

    public async Task<CalendarEvent?> GetNextEventAsync()
    {
        try
        {
            var store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
            var appointments = await store.FindAppointmentsAsync(
                DateTimeOffset.Now,
                TimeSpan.FromDays(1));

            var next = appointments
                .OrderBy(a => a.StartTime)
                .FirstOrDefault();

            if (next == null)
            {
                NextEvent = null;
                NextEventChanged?.Invoke(null);
                return null;
            }

            NextEvent = new CalendarEvent
            {
                Subject = next.Subject,
                StartTime = next.StartTime,
                Duration = next.Duration,
                Location = next.Location
            };

            NextEventChanged?.Invoke(NextEvent);
            return NextEvent;
        }
        catch
        {
            return null;
        }
    }
}

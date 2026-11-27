//summary//
// Provides the request identifier displayed by the application error view.
//summary//
//-------------------Start Of File------------------//
namespace CloudStore.Models
{
    public class ErrorViewModel
    {
        public string? RequestId { get; set; }

        public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
    }
}
//-------------------End Of File------------------//

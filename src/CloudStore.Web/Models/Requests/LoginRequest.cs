//summary//
// Bounds login input without ever redisplaying a submitted password.
//summary//
//-------------------Start Of File------------------//
using System.ComponentModel.DataAnnotations;
namespace CloudStore.Models.Requests;
public class LoginRequest
{
    [Required, StringLength(256)]
    public string Username { get; set; } = "";
    [Required, StringLength(256), DataType(DataType.Password)]
    public string Password { get; set; } = "";
}
//-------------------End Of File------------------//

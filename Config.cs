using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace PlayerConnectInfo;

public class PlayerConnectInfoConfig : BasePluginConfig
{
    public override int Version { get; set; } = 2;

    /// <summary>Писать «подключается» — когда клиент начал заход и качает аддоны.</summary>
    [JsonPropertyName("ShowConnecting")] public bool ShowConnecting { get; set; } = true;

    /// <summary>Писать «подключился» — когда игрок реально доехал до сервера.</summary>
    [JsonPropertyName("ShowConnect")] public bool ShowConnect { get; set; } = true;

    /// <summary>Писать в чат о выходе.</summary>
    [JsonPropertyName("ShowDisconnect")] public bool ShowDisconnect { get; set; } = true;

    /// <summary>
    /// Причины отключения, которые считаются «переподключением» и не объявляются.
    /// 54 = NETWORK_DISCONNECT_LOOPSHUTDOWN — именно её выдаёт цикл докачки
    /// аддонов в MultiAddonManager. 53 = NETWORK_DISCONNECT_RECONNECTION.
    /// </summary>
    [JsonPropertyName("ReconnectReasons")] public List<int> ReconnectReasons { get; set; } = new() { 53, 54 };

    /// <summary>
    /// Причины, по которым уход объявляется СРАЗУ, без ожидания. Это явные
    /// расставания, после которых игрок не вернётся сам собой.
    /// 2 = DISCONNECT_BY_USER (вышел сам), 39 = KICKED, 40 = BANADDED,
    /// 41 = KICKBANADDED, 149 = REJECT_BANNED.
    /// </summary>
    [JsonPropertyName("ImmediateLeaveReasons")] public List<int> ImmediateLeaveReasons { get; set; } = new() { 2, 39, 40, 41, 149 };

    /// <summary>
    /// Сколько ждать перед объявлением об уходе, если причина неизвестная.
    /// Если игрок вернётся за это время, сообщения не будет вообще.
    ///
    /// Должно перекрывать докачку аддона: клиент уходит качать и возвращается
    /// через десятки секунд. При 10 с окно истекало посреди загрузки, плагин
    /// писал «отключился», а вернувшийся игрок объявлялся заново — отсюда пары
    /// «отключился/подключился» при обычном заходе.
    /// </summary>
    [JsonPropertyName("SettleSeconds")] public int SettleSeconds { get; set; } = 90;

    /// <summary>
    /// Через сколько минут забывать игрока, который ушёл во время докачки
    /// аддона и не вернулся. Сообщения при этом не будет — он и не «уходил»
    /// в обычном смысле, а завис на загрузке.
    /// </summary>
    [JsonPropertyName("ForgetAfterMinutes")] public int ForgetAfterMinutes { get; set; } = 5;

    /// <summary>
    /// Админы сервера (IksAdmin) заходят и выходят молча: ни «подключается»,
    /// ни «подключился», ни «отключился».
    /// </summary>
    [JsonPropertyName("SilentAdmins")] public bool SilentAdmins { get; set; } = true;

    /// <summary>
    /// Админ сразу после захода скрыт из таба — то же состояние, что после !hide
    /// в IksAdmin, но без фейкового «отключился», которое !hide пишет в чат.
    /// </summary>
    [JsonPropertyName("AutoHideAdmins")] public bool AutoHideAdmins { get; set; } = true;

    /// <summary>Подробный лог в консоль: видно, какая причина пришла и что с ней сделали.</summary>
    [JsonPropertyName("Debug")] public bool Debug { get; set; } = false;
}

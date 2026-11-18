using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;

namespace FisherLogger
{
    // =====================================================================================
    // ADVISOR-01: ИИ-агент по API (ключ пользователя).
    //
    // Как агент взаимодействует с программой:
    //  * Агент НЕ получает базу и не может ничего менять. Он вызывает «инструменты» программы —
    //    функции только для чтения поверх BiteAnalytics: сводка, совет по виду, водоём, формула, последние уловы.
    //  * В запрос уходят только эти сводки (числа, названия видов/водоёмов/наживок). Сырые пакеты,
    //    логин/пароль игры, uid предметов и путь к файлам не отправляются.
    //  * Системная инструкция требует опираться только на ответы инструментов и прямо говорить
    //    «данных мало», если инструмент так сообщает.
    //  * Поддерживаются два формата API: OpenAI-совместимый (DeepSeek, OpenAI, OpenRouter, локальные
    //    серверы) — /chat/completions с tools; Anthropic — /v1/messages с tools.
    // =====================================================================================

    public sealed class AgentTool
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public JsonObject Schema { get; init; } = new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public Func<JsonObject, string> Run { get; init; } = _ => "{}";
    }

    public sealed class AgentTurn
    {
        public string Role { get; init; } = "user";   // user | assistant
        public string Text { get; init; } = "";
    }

    public sealed class AgentReply
    {
        public bool Ok { get; init; }
        public string Text { get; init; } = "";
        public List<string> ToolCalls { get; init; } = new();
        public string Error { get; init; } = "";
    }

    public sealed class AgentClient
    {
        public const string SystemPrompt =
            "Ты — помощник рыболова в игре The Fisher Online внутри программы FisherLogger. " +
            "Отвечай по-русски, коротко и по делу. Факты о данных пользователя бери ТОЛЬКО из ответов инструментов. " +
            "Если инструмент пишет «мало данных», «совета нет» или «формулы нет» — так и скажи и назови, чего не хватает; " +
            "не придумывай места, наживки, вероятности и формулы. Различай: «совет» (достаточно данных), «мало данных», " +
            "объявления других игроков (только активность, не вероятность). Уровни CONFIRMED/PROBABLE передавай как «подтверждено»/«вероятно». " +
            "Если get_species_advice вернул «есть_совет: false» — вызови get_reference_hints и подай ответ как справочные данные (не статистика пользователя), " +
            "ориентир «с чего начать», но никогда не как подтверждённую вероятность или совет. " +
            "Если в ответе инструмента уже есть поле «справочно» — используй его (отдельный вызов не нужен): назови водоёмы, точки, наживки, время и глубину " +
            "из него и явно скажи, что это справочные данные по чужим уловам. Если вид не найден в данных пользователя — тоже дай справочные данные. " +
            "Ты не управляешь игрой и ничего не меняешь в программе.";

        /// <summary>COACH-01: короткая инструкция для разбора сессии (один запрос, без инструментов — дешевле в 3–6 раз).</summary>
        public const string CoachPrompt =
            "Ты — тренер рыболова в игре The Fisher Online. Тебе дана сжатая сводка текущей рыбалки из программы FisherLogger. " +
            "Ответь по-русски, не больше 5 коротких пунктов, каждый — конкретное действие (какую удочку перезабросить, что сменить: крючок, наживку, точку, глубину, прикорм) " +
            "и почему — со ссылкой на числа из сводки. Опирайся ТОЛЬКО на сводку. Если данных мало — так и скажи и предложи опыт: менять один фактор на одной из удочек. " +
            "Сравнивай между собой ТОЛЬКО удочки, которые сводка называет похожими (≥50 % совпадений), и объясняй результат их разницей. " +
            "Удочкам, которые сводка называет непохожими, НЕ советуй наживку или снасть другой удочки: у них может быть другая целевая рыба (смотри, что на них поймано). " +
            "«Пустая поклёвка» — рыба тронула наживку, но не засеклась. «Вероятно» — не доказано. Не придумывай механику игры, вероятности и формулы.";

        private readonly AgentSettings _s;
        private readonly string _key;
        private string? _system;
        /// <summary>COACH-01: токены последнего запроса (сумма по всем раундам; 0 — сервер не сообщил).</summary>
        public int LastTokens { get; private set; }
        private readonly HttpClient _http;
        private readonly TimeSpan _timeout;
        /// <summary>AUDIT-04 (A5): один общий HttpClient на программу (без утечки сокетов на каждый запрос) и предел размера ответа 4 МБ.</summary>
        public const int MaxResponseBytes = 4 * 1024 * 1024;
        private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = System.Threading.Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxResponseBytes };
        private static readonly JsonSerializerOptions Js = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

        public AgentClient(AgentSettings s, string apiKey, HttpMessageHandler? handler = null)
        {
            _s = s; _key = apiKey;
            _http = handler != null ? new HttpClient(handler, false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxResponseBytes } : SharedHttp;
            _timeout = TimeSpan.FromSeconds(Math.Clamp(s.TimeoutSeconds, 10, 300));   // ожидание — на каждый запрос (общий клиент без своего таймаута)
        }

        /// <summary>COACH-01: предел длины ответа (экономия токенов).</summary>
        private int MaxOut => _system == CoachPrompt ? Math.Clamp(_s.CoachMaxTokens, 100, 2000) : Math.Clamp(_s.MaxOutputTokens, 200, 4000);

        public string Endpoint
        {
            get
            {
                string b = (_s.BaseUrl ?? "").Trim().TrimEnd('/');
                if (_s.Provider == AgentProvider.Anthropic) return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b + "/messages" : b + "/v1/messages";
                return b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? b : b + "/chat/completions";
            }
        }

        public async Task<AgentReply> AskAsync(IReadOnlyList<AgentTurn> history, string question, IReadOnlyList<AgentTool> tools, CancellationToken ct = default)
        {
            var calls = new List<string>();
            LastTokens = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(_s.BaseUrl) || !Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                    return new AgentReply { Error = "Не указан или неверен адрес API (Настройки → ИИ-агент)." };
                if (string.IsNullOrWhiteSpace(_s.Model)) return new AgentReply { Error = "Не указана модель (Настройки → ИИ-агент)." };
                if (uri.Scheme == "http" && !uri.IsLoopback)
                    return new AgentReply { Error = "Без шифрования (http://) разрешён только локальный сервер на этом компьютере — иначе ключ уйдёт открытым текстом." };
                return _s.Provider == AgentProvider.Anthropic
                    ? await AskAnthropic(history, question, tools, calls, ct)
                    : await AskOpenAi(history, question, tools, calls, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new AgentReply { Error = $"Нет ответа за {_s.TimeoutSeconds} с.", ToolCalls = calls }; }
            catch (OperationCanceledException) { return new AgentReply { Error = "Запрос отменён.", ToolCalls = calls }; }
            catch (HttpRequestException ex) { return new AgentReply { Error = "Сеть: " + ex.Message, ToolCalls = calls }; }
            catch (Exception ex) { return new AgentReply { Error = "Ошибка агента: " + ex.Message, ToolCalls = calls }; }
        }

        private string RunTool(IReadOnlyList<AgentTool> tools, string name, JsonObject args, List<string> calls)
        {
            calls.Add(name + (args.Count > 0 ? " " + args.ToJsonString(Js) : ""));
            var t = tools.FirstOrDefault(x => x.Name == name);
            if (t == null) return "{\"error\":\"неизвестный инструмент\"}";
            try { return t.Run(args); } catch (Exception ex) { return JsonSerializer.Serialize(new { error = ex.Message }, Js); }
        }

        private async Task<(JsonNode? body, string? err)> Post(JsonObject body, Action<HttpRequestMessage> headers, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body.ToJsonString(Js), Encoding.UTF8, "application/json") };
            headers(req);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            using var resp = await _http.SendAsync(req, cts.Token);
            string text = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                string msg = text;
                try { var j = JsonNode.Parse(text); msg = j?["error"]?["message"]?.GetValue<string>() ?? j?["error"]?.ToString() ?? text; } catch { }
                if (msg.Length > 300) msg = msg[..300] + "…";
                string hint = (int)resp.StatusCode switch { 401 => " (неверный API-ключ)", 402 => " (нет средств на счёте API)", 403 => " (доступ запрещён)", 404 => " (неверный адрес или модель)", 429 => " (превышен лимит запросов)", _ => "" };
                return (null, $"API вернул {(int)resp.StatusCode}{hint}: {msg}");
            }
            var node = JsonNode.Parse(text);
            try
            {
                var u = node?["usage"];
                int t = u?["total_tokens"]?.GetValue<int>() ?? 0;
                if (t == 0) t = (u?["prompt_tokens"]?.GetValue<int>() ?? 0) + (u?["completion_tokens"]?.GetValue<int>() ?? 0);
                if (t == 0) t = (u?["input_tokens"]?.GetValue<int>() ?? 0) + (u?["output_tokens"]?.GetValue<int>() ?? 0);
                if (t == 0) t = (body.ToJsonString(Js).Length + text.Length) / 3;   // сервер не сообщил — грубая оценка (≈3 символа на токен)
                LastTokens += t;
            }
            catch { }
            return (node, null);
        }

        /// <summary>COACH-01: один запрос без инструментов с инструкцией тренера и ограничением длины ответа.</summary>
        public async Task<AgentReply> AskCoachAsync(string summary, CancellationToken ct = default)
        {
            _system = CoachPrompt;
            try { return await AskAsync(Array.Empty<AgentTurn>(), summary, Array.Empty<AgentTool>(), ct); }
            finally { _system = null; }
        }

        private async Task<AgentReply> AskOpenAi(IReadOnlyList<AgentTurn> history, string q, IReadOnlyList<AgentTool> tools, List<string> calls, CancellationToken ct)
        {
            var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = _system ?? SystemPrompt } };
            foreach (var h in history) msgs.Add(new JsonObject { ["role"] = h.Role == "assistant" ? "assistant" : "user", ["content"] = h.Text });
            msgs.Add(new JsonObject { ["role"] = "user", ["content"] = q });
            var toolArr = new JsonArray();
            foreach (var t in tools)
                toolArr.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Schema.DeepClone() } });

            for (int round = 0; round <= _s.MaxToolCalls; round++)
            {
                var body = new JsonObject { ["model"] = _s.Model, ["messages"] = msgs.DeepClone(), ["temperature"] = _s.Temperature, ["max_tokens"] = MaxOut };
                if (toolArr.Count > 0 && round < _s.MaxToolCalls) body["tools"] = toolArr.DeepClone();
                var (resp, err) = await Post(body, r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key), ct);
                if (err != null) return new AgentReply { Error = err, ToolCalls = calls };
                var msg = resp?["choices"]?[0]?["message"] as JsonObject;
                if (msg == null) return new AgentReply { Error = "Непонятный ответ API (нет choices[0].message).", ToolCalls = calls };
                if (msg["tool_calls"] is JsonArray tc && tc.Count > 0)
                {
                    msgs.Add(msg.DeepClone());
                    foreach (var c in tc)
                    {
                        string name = c?["function"]?["name"]?.GetValue<string>() ?? "";
                        JsonObject args = new();
                        try { if (c?["function"]?["arguments"]?.GetValue<string>() is { Length: > 0 } a && JsonNode.Parse(a) is JsonObject o) args = o; } catch { }
                        msgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c?["id"]?.GetValue<string>() ?? "", ["content"] = RunTool(tools, name, args, calls) });
                    }
                    continue;
                }
                string text = msg["content"]?.GetValue<string>() ?? "";
                return new AgentReply { Ok = true, Text = text.Trim(), ToolCalls = calls };
            }
            return new AgentReply { Error = $"Агент превысил лимит вызовов инструментов ({_s.MaxToolCalls}).", ToolCalls = calls };
        }

        private async Task<AgentReply> AskAnthropic(IReadOnlyList<AgentTurn> history, string q, IReadOnlyList<AgentTool> tools, List<string> calls, CancellationToken ct)
        {
            var msgs = new JsonArray();
            foreach (var h in history) msgs.Add(new JsonObject { ["role"] = h.Role == "assistant" ? "assistant" : "user", ["content"] = h.Text });
            msgs.Add(new JsonObject { ["role"] = "user", ["content"] = q });
            var toolArr = new JsonArray();
            foreach (var t in tools) toolArr.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = t.Schema.DeepClone() });

            for (int round = 0; round <= _s.MaxToolCalls; round++)
            {
                var body = new JsonObject { ["model"] = _s.Model, ["max_tokens"] = MaxOut, ["system"] = _system ?? SystemPrompt, ["messages"] = msgs.DeepClone(), ["temperature"] = Math.Min(1.0, _s.Temperature) };
                if (toolArr.Count > 0 && round < _s.MaxToolCalls) body["tools"] = toolArr.DeepClone();
                var (resp, err) = await Post(body, r => { r.Headers.Add("x-api-key", _key); r.Headers.Add("anthropic-version", "2023-06-01"); }, ct);
                if (err != null) return new AgentReply { Error = err, ToolCalls = calls };
                if (resp?["content"] is not JsonArray content) return new AgentReply { Error = "Непонятный ответ API (нет content).", ToolCalls = calls };
                var uses = content.Where(b => b?["type"]?.GetValue<string>() == "tool_use").ToList();
                if (uses.Count > 0 && resp?["stop_reason"]?.GetValue<string>() == "tool_use")
                {
                    msgs.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });
                    var results = new JsonArray();
                    foreach (var u in uses)
                        results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = u?["id"]?.GetValue<string>() ?? "",
                                                     ["content"] = RunTool(tools, u?["name"]?.GetValue<string>() ?? "", u?["input"] as JsonObject ?? new JsonObject(), calls) });
                    msgs.Add(new JsonObject { ["role"] = "user", ["content"] = results });
                    continue;
                }
                string text = string.Join("\n", content.Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b?["text"]?.GetValue<string>() ?? ""));
                return new AgentReply { Ok = true, Text = text.Trim(), ToolCalls = calls };
            }
            return new AgentReply { Error = $"Агент превысил лимит вызовов инструментов ({_s.MaxToolCalls}).", ToolCalls = calls };
        }
    }

    /// <summary>Инструменты агента: функции только для чтения поверх снимка BiteAnalytics.</summary>
    public static class AdvisorTools
    {
        private static readonly JsonSerializerOptions Js = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
        private static JsonObject Obj(params (string name, string type, string desc)[] props)
        {
            var p = new JsonObject(); var req = new JsonArray();
            foreach (var (n, t, d) in props) { p[n] = new JsonObject { ["type"] = t, ["description"] = d }; req.Add(n); }
            var o = new JsonObject { ["type"] = "object", ["properties"] = p };
            if (props.Length > 0) o["required"] = req;
            return o;
        }
        private static string Arg(JsonObject a, string n) { try { return a[n]?.GetValue<string>()?.Trim() ?? ""; } catch { return a[n]?.ToString() ?? ""; } }

        public static uint? FindSpecies(BiteAnalytics an, string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return null;
            var all = an.KnownSpecies();
            var exact = all.FirstOrDefault(s => string.Equals(s.name, q, StringComparison.OrdinalIgnoreCase));
            if (exact.name != null) return exact.id;
            var part = all.FirstOrDefault(s => s.name.Contains(q, StringComparison.OrdinalIgnoreCase) || q.Contains(s.name, StringComparison.OrdinalIgnoreCase));
            return part.name != null ? part.id : null;
        }

        /// <summary>FIX-03: компактный справочный блок (топ мест/наживок) для встраивания в ответ get_species_advice.</summary>
        public static object? ReferenceBlock(ReferenceProvider? prov, ReferenceHints? h, uint? fishLangId)
        {
            if (prov == null || h == null) return null;
            return new
            {
                источник = ReferenceProvider.SourceNote,
                пояснение = "агрегированные чужие уловы (fisher-hub); ориентир «с чего начать», НЕ вероятность клёва и НЕ ваш совет",
                вид = prov.DisplayName(h, fishLangId), вид_англ = h.FishEn, всего_поимок = h.Count,
                локации = h.Locations.OrderByDescending(l => l.Count).Take(6).Select(l => new { название = l.Name, location_id = l.LocationId, поимок = l.Count, точки = l.Points.Take(6) }),
                наживки = h.Baits.OrderByDescending(b => b.Count).Take(8).Select(b => new { название = b.Name, поимок = b.Count }),
                приманки = h.Lures.OrderByDescending(b => b.Count).Take(5).Select(b => new { название = b.Name, поимок = b.Count }),
                периоды = h.Periods, погода = h.Weather, крючки = h.Hooks,
                глубина = new { min = h.Depth.Min, avg = h.Depth.Avg, max = h.Depth.Max, n = h.Depth.N, единицы = "не проверены" }
            };
        }

        public static List<AgentTool> Build(Func<BiteAnalytics> analytics, Func<ModelRegistry> registry, Func<ReferenceProvider>? reference = null, Func<string>? session = null)
        {
            ReferenceProvider? shared = null;
            Func<ReferenceProvider> refs = reference ?? (() => shared ??= new ReferenceProvider());
            ReferenceProvider? TryRefs() { try { return refs(); } catch { return null; } }
            var list = new List<AgentTool>
            {
                new()
                {
                    Name = "get_overview", Description = "Сводка данных пользователя: сколько забросов, поклёвок, уловов, видов, водоёмов; состояние формулы клёва и чего не хватает.",
                    Schema = Obj(), Run = _ => JsonSerializer.Serialize(analytics().Overview(registry()), Js)
                },
                new()
                {
                    Name = "list_species", Description = "Список видов рыб из данных пользователя: свои поимки и объявления других игроков.",
                    Schema = Obj(), Run = _ => JsonSerializer.Serialize(analytics().KnownSpecies().Take(60).Select(s => new { вид = s.name, мои_поимки = s.own, объявления_других = s.pub }), Js)
                },
                new()
                {
                    Name = "get_species_advice", Description = "Где, на что и в какое игровое время ловится вид — только по данным пользователя, с указанием достаточности данных.",
                    Schema = Obj(("fish", "string", "название вида по-русски, например «Карп»")),
                    Run = a =>
                    {
                        var an = analytics(); string fishArg = Arg(a, "fish"); var id = FindSpecies(an, fishArg);
                        var prov = TryRefs();
                        if (id == null)
                        {
                            // FIX-03: вида нет в своих данных — самый частый случай «мало данных»; справочник прикладывается сразу
                            var hr = prov?.GetHints(fishArg);
                            return JsonSerializer.Serialize(new
                            {
                                результат = "вид не найден в данных пользователя", есть_совет = false,
                                известные_виды = an.KnownSpecies().Take(30).Select(s => s.name),
                                справочно = ReferenceBlock(prov, hr, null),
                                справочно_нет = hr == null ? "вида нет и в справочнике" : null
                            }, Js);
                        }
                        var adv = an.Advise(id.Value);
                        // FIX-03: при недостатке своих данных справочный блок прикладывается детерминированно, а не по желанию модели
                        var hs = adv.HasAdvice ? null : prov?.GetHintsForSpecies(id.Value, adv.Fish);
                        return JsonSerializer.Serialize(new
                        {
                            вид = adv.Fish, есть_совет = adv.HasAdvice, мои_поимки = adv.OwnCatches, объявления_других = adv.PublicSeen,
                            где_встречается = adv.Presence.Select(p => new { водоём = p.Waterbody, мои = p.OwnCatches, у_других = p.PublicSeen, макс_класс = p.MaxWindex, участков = p.Zones }),
                            группы = adv.Rows.Take(25).Select(r => new { r.Group, r.Item, забросов = r.Attempts, поимок = r.Successes, интервал_80 = $"{r.Lo:P0}–{r.Hi:P0}", вывод = r.Verdict, уровень = r.Level }),
                            примечания = adv.Notes,
                            справочно = adv.HasAdvice ? null : ReferenceBlock(prov, hs, id.Value),
                            справочно_нет = !adv.HasAdvice && hs == null ? "вида нет в справочнике" : null
                        }, Js);
                    }
                },
                new()
                {
                    // UIFIX-02: прикормка — по вопросу «чем прикормить <рыбу>»
                    Name = "get_groundbait_recipe", Description = "Рецепт прикормки под вид: основа, которая по файлам игры привлекает эту рыбу, + аромат, добавка и наживка (по описаниям игры и журналу пользователя), цена; варианты «по данным игры», «по вашему журналу», «готовая/подешевле». Если игра не связывает прикормку с видом — так и сказано.",
                    Schema = Obj(("fish", "string", "название вида по-русски, например «Линь»")),
                    Run = a =>
                    {
                        var an = analytics(); string fishArg = Arg(a, "fish"); var id = FindSpecies(an, fishArg);
                        if (id == null) return JsonSerializer.Serialize(new { результат = "вид не найден в данных пользователя", известные_виды = an.KnownSpecies().Take(30).Select(s => s.name) }, Js);
                        var r = GroundbaitCalc.For(id.Value, an.DbPath);
                        return JsonSerializer.Serialize(new
                        {
                            вид = an.FishName(id.Value), игра_знает_прикормку_для_вида = r.GameKnows, примечание = r.Note,
                            рецепты = r.Recipes.Select(x => new
                            {
                                вариант = x.Title, состав = x.Parts.Select(p => new { слот = p.Slot, предмет = p.Name, цена_кр = p.Cost, источник = p.Source }),
                                цена_набора_кр = x.Cost, журнал = x.N > 0 ? $"нужная рыба на {x.K} из {x.N} забросов с этой основой/смесью (частота, не вероятность)" : null, почему = x.Why
                            }),
                            важно = "Привлечение рыбы задаёт основа (подтверждено файлами игры); аромат, добавка и наживка — по описаниям игры и журналу, это не доказанное влияние."
                        }, Js);
                    }
                },
                new()
                {
                    Name = "get_waterbody_species", Description = "Какие виды водятся/встречаются на водоёме: подтверждено ли (свои уловы, список игры, справочник) и сколько своих поимок и объявлений. Отсутствие вида в списке не доказывает, что его там нет.",
                    Schema = Obj(("waterbody", "string", "название водоёма или его часть")),
                    Run = a =>
                    {
                        var an = analytics(); string q = Arg(a, "waterbody");
                        var rows = an.Presence().Where(p => q.Length == 0 || p.Waterbody.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (rows.Count == 0) return JsonSerializer.Serialize(new { результат = "водоём не найден в данных", известные_водоёмы = an.Presence().Select(p => p.Waterbody).Distinct() }, Js);
                        // AUDIT-02: объявления места поимки не доказывают; «водится» — по своим уловам, списку игры и справочнику
                        var locIds = rows.Where(p => p.LocationId.HasValue).Select(p => p.LocationId!.Value).Distinct().ToList();
                        var extra = locIds.SelectMany(l => an.Habitat.SpeciesAt(l).Where(f => !rows.Any(p => p.LocationId == l && p.FishId == f))
                                          .Select(f => new { водоём = an.LocName(l), вид = an.FishName(f), мои = 0, у_других = 0, макс_класс = -1, водится = HabitatIndex.SourceText(an.Habitat.Sources(f, l)) }));
                        return JsonSerializer.Serialize(new
                        {
                            примечание = "«водится» — подтверждено своими уловами, списком игры или справочником; объявления других игроков не доказывают место поимки; списки неполные",
                            виды = rows.Take(60).Select(p => new { водоём = p.Waterbody, вид = p.Fish, мои = p.OwnCatches, у_других = p.PublicSeen, макс_класс = p.MaxWindex,
                                                                    водится = p.LocationId.HasValue && an.Habitat.Lives(p.FishId, p.LocationId) ? HabitatIndex.SourceText(an.Habitat.Sources(p.FishId, p.LocationId.Value)) : "не подтверждено" })
                                       .Concat(extra.Take(40))
                        }, Js);
                    }
                },
                new()
                {
                    Name = "get_formula", Description = "Состояние формулы клёва: найдена ли, насколько лучше среднего, признаки и веса, история версий.",
                    Schema = Obj(),
                    Run = _ =>
                    {
                        var reg = registry(); var act = reg.ActiveModel;
                        return JsonSerializer.Serialize(new
                        {
                            статус = analytics().Overview(reg).FormulaStatus,
                            активная = act == null ? null : new { версия = act.Version, забросов = act.N, улучшение = act.Improvement, признаки = act.Features.Zip(act.Weights, (f, w) => new { признак = f, вес = Math.Round(w, 3) }),
                                                                  проверка_на_новых = act.ForwardN > 0 ? $"{act.ForwardN} забросов, log-loss {act.ForwardLogLoss:0.000} против среднего {act.ForwardBaseline:0.000}" : "новых забросов ещё нет" },
                            история = reg.History.TakeLast(10).Select(h => new { версия = h.Version, создана = h.CreatedAt, принята = h.Accepted, активна = h.Active, h.Status, h.DeactivatedReason })
                        }, Js);
                    }
                },
                new()
                {
                    Name = "get_reference_hints", Description = "Справочные подсказки «где / на что / когда» из агрегированных чужих уловов (fisher-hub). Это НЕ ваша статистика и НЕ вероятность клёва — только ориентир, когда своих данных мало.",
                    Schema = Obj(("fish", "string", "название вида по-русски или по-английски, например «Карась» / «Crucian»")),
                    Run = a =>
                    {
                        var prov = refs();
                        string fish = Arg(a, "fish");
                        var h = prov.GetHints(fish);
                        if (h == null)
                            return JsonSerializer.Serialize(new { результат = "вид не найден в справочнике", источник = ReferenceProvider.SourceNote, похожие_названия = prov.ClosestNames(fish, 8) }, Js);
                        return JsonSerializer.Serialize(new
                        {
                            источник = ReferenceProvider.SourceNote,
                            вид = prov.DisplayName(h), вид_англ = h.FishEn, всего_поимок = h.Count,
                            локации = h.Locations.Select(l => new { название = l.Name, location_id = l.LocationId, поимок = l.Count, точки = l.Points }),
                            наживки = h.Baits.Select(b => new { название = b.Name, поимок = b.Count }),
                            приманки = h.Lures.Select(b => new { название = b.Name, поимок = b.Count }),
                            периоды = h.Periods, погода = h.Weather, крючки = h.Hooks,
                            глубина = new { min = h.Depth.Min, avg = h.Depth.Avg, max = h.Depth.Max, n = h.Depth.N }
                        }, Js);
                    }
                },
                new()
                {
                    Name = "get_recent_attempts", Description = "Последние забросы пользователя (до 20): время, водоём, наживка, исход, пойманный вид и вес.",
                    Schema = Obj(),
                    Run = _ =>
                    {
                        var an = analytics();
                        return JsonSerializer.Serialize(an.Attempts.OrderByDescending(x => x.Ts).Take(20).Select(x => new
                        {
                            время = x.Ts.ToString("yyyy-MM-dd HH:mm"), водоём = an.LocName(x.Location), наживка = x.Bait, исход = x.Outcome, вид = x.Fish, вес_г = x.Weight, класс = x.Windex,
                            // COACH-01: где лежала наживка, глубина дна, время в воде, пустые поклёвки, крючок
                            удочка = x.Rod + 1, тип = SoakCurve.RodName(x.RodCode), клетка_5м = x.Cell5, глубина_дна_м = x.BottomDepthM is double bd ? Math.Round(bd, 1) : (double?)null,
                            в_воде_мин = x.SoakMin is double sm ? Math.Round(sm, 1) : (double?)null, пустых = x.EmptyBites, крючок = x.Hook
                        }), Js);
                    }
                }
            };
            // COACH-01: разбор текущей рыбалки по удочкам (сжатая сводка; та же, что видит тренер в оверлее)
            if (session != null)
                list.Add(new()
                {
                    Name = "analyze_session", Description = "Текущая (последняя) рыбалка по удочкам: время в воде, поклёвки и пустые поклёвки, крючок, наживка, подсказки программы и кривая «поклёвки по минуте в воде». Используй, когда спрашивают про текущую рыбалку или «что сейчас сделать».",
                    Schema = Obj(),
                    Run = _ => session()
                });
            return list;
        }
    }
}

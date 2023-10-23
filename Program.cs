using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Azure.Messaging;
using Azure;
using Azure.AI.OpenAI;

var builder = WebApplication.CreateBuilder(args);

//Get ACS Connection String from appsettings.json
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
//Call Automation Client
var client = new CallAutomationClient(connectionString: acsConnectionString);

//Grab the Cognitive Services endpoint from appsettings.json
var cognitiveServicesEndpoint = builder.Configuration.GetValue<string>("CognitiveServiceEndpoint");

//Register and make CallAutomationClient accessible via dependency injection
builder.Services.AddSingleton(client);
var app = builder.Build();

var devTunnelUri = builder.Configuration.GetValue<string>("DevTunnelUri");
var maxTimeout = 2;

static FunctionDefinition GetFunctionDefinition()
{
    return new FunctionDefinition()
    {
        Name = "microsoft_search_api",
        Description = "手順やルールのドキュメントに関する質問をされるとMicrosoft Graph Search APIを用いて検索をします。",
        Parameters = BinaryData.FromObjectAsJson(
        new
        {
            Type = "object",
            Properties = new
            {
                Requests = new
                {
                    Type = "object",
                    Properties = new
                    {
                        EntityTypes = new
                        {
                            Type = "array",
                            Description = "検索の対象にするリソースの種類。不明な場合はすべての項目を含めます。",
                            Items = new
                            {
                                Type = "string",
                                Enum = new[] { "site", "list", "listItem", "drive", "driveItem", "message", "event" }
                            }
                        },
                        Query = new
                        {
                            Type = "object",
                            Properties = new
                            {
                                QueryString = new
                                {
                                    Type = "string",
                                    Description = "検索キーワード。半角スペース区切り。"
                                }
                            }
                        }
                    }
                }
            },
            Required = new[] { "requests" },
        },
        new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
    };
}

app.MapGet("/", () => "Hello ACS CallAutomation!");

app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation($"Incoming Call event received.");

        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }

        var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
        var callerId = Helper.GetCallerId(jsonObject);
        var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
        var callbackUri = new Uri(devTunnelUri + $"/api/callbacks/{Guid.NewGuid()}?callerId={callerId}");
        var options = new AnswerCallOptions(incomingCallContext, callbackUri)
        {
            CognitiveServicesEndpoint = new Uri(cognitiveServicesEndpoint)
        };

        AnswerCallResult answerCallResult = await client.AnswerCallAsync(options);
        Console.WriteLine($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");

        //Use EventProcessor to process CallConnected event
        var answer_result = await answerCallResult.WaitForEventProcessorAsync();
        if (answer_result.IsSuccess)
        {
            Console.WriteLine($"Call connected event received for connection id: {answer_result.SuccessResult.CallConnectionId}");
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();
            await HandleRecognizeAsync(callConnectionMedia, callerId, "お電話ありがとうございます。何をお手伝いしましょう？");
        }

        client.GetEventProcessor().AttachOngoingEventProcessor<PlayCompleted>(answerCallResult.CallConnection.CallConnectionId, async (playCompletedEvent) =>
        {
            Console.WriteLine($"Play completed event received for connection id: {playCompletedEvent.CallConnectionId}. Hanging up call...");
            await HandleHangupAsync(answerCallResult.CallConnection);
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<PlayFailed>(answerCallResult.CallConnection.CallConnectionId, async (playFailedEvent) =>
        {
            Console.WriteLine($"Play failed event received for connection id: {playFailedEvent.CallConnectionId}. Hanging up call...");
            await HandleHangupAsync(answerCallResult.CallConnection);
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<RecognizeCompleted>(answerCallResult.CallConnection.CallConnectionId, async (recognizeCompletedEvent) =>
        {
            Console.WriteLine($"Recognize completed event received for connection id: {recognizeCompletedEvent.CallConnectionId}");
            var speech_result = recognizeCompletedEvent.RecognizeResult as SpeechResult;

            if (!string.IsNullOrWhiteSpace(speech_result?.Speech))
            {
                Console.WriteLine($"Recognized speech: {speech_result.Speech}");
                var chatGPTResponse = await GetChatGPTResponse(speech_result?.Speech);
                await HandleChatResponse(chatGPTResponse, answerCallResult.CallConnection.GetCallMedia(), callerId, logger);
            }
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<RecognizeFailed>(answerCallResult.CallConnection.CallConnectionId, async (recognizeFailedEvent) =>
        {
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();

            if (MediaEventReasonCode.RecognizeInitialSilenceTimedOut.Equals(recognizeFailedEvent.ResultInformation.SubCode.Value.ToString()) && maxTimeout > 0)
            {
                Console.WriteLine($"Recognize failed event received for connection id: {recognizeFailedEvent.CallConnectionId}. Retrying recognize...");
                maxTimeout--;
                await HandleRecognizeAsync(callConnectionMedia, callerId, "恐れ入りますが声が聞こえません。電話口にいらっしゃいますか？");
            }
            else
            {
                Console.WriteLine($"Recognize failed event received for connection id: {recognizeFailedEvent.CallConnectionId}. Playing goodbye message...");
                await HandlePlayAsync(callConnectionMedia);
            }
        });
    }
    return Results.Ok();
});

// api to handle call back events
app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    CallAutomationClient callAutomationClient,
    ILogger<Program> logger) =>
{
    var eventProcessor = client.GetEventProcessor();
    eventProcessor.ProcessEvents(cloudEvents);
    return Results.Ok();
});

async Task HandleChatResponse(string chatResponse, CallMedia callConnectionMedia, string callerId, ILogger logger)
{
    var chatGPTResponseSource = new TextSource(chatResponse)
    {
        VoiceName = "ja-JP-DaichiNeural"
    };

    var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(callerId))
        {
            InterruptPrompt = false,
            InitialSilenceTimeout = TimeSpan.FromSeconds(15),
            Prompt = chatGPTResponseSource,
            OperationContext = "OpenAISample",
            EndSilenceTimeout = TimeSpan.FromMilliseconds(500),
            SpeechLanguage = "ja-JP"
        };

    var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
}

async Task<string> CallMicrosoftSearchAsync(SearchInput input)
{
    // エンドポイントとHttpClientを設定
    // var endpoint = "https://call-app-sample.azurewebsites.net/api/MicrosoftSearch?code=DZxos3eTCDgYiUzXwgQTfLGvnd2uZvjxXLtxFk1quwwuAzFu3JAJjg==";
    var endpoint = builder.Configuration.GetValue<string>("FunctionsEndpoint");
    var httpClient = new HttpClient();

    // inputオブジェクトをJSONにシリアル化
    var jsonContent = JsonSerializer.Serialize(input);
    var httpContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

    // POSTリクエストを実行
    var httpResponse = await httpClient.PostAsync(endpoint, httpContent);

    // 応答をチェックし、応答コンテンツを返す
    httpResponse.EnsureSuccessStatusCode();
    var responseContent = await httpResponse.Content.ReadAsStringAsync();
    return responseContent;
}

async Task<string> GetChatGPTResponse(string speech_input)
{
    var key = builder.Configuration.GetValue<string>("AzureOpenAIServiceKey");
    var endpoint = builder.Configuration.GetValue<string>("AzureOpenAIServiceEndpoint");

    var ai_client = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));
    //var ai_client = new OpenAIClient(openAIApiKey: ""); //Use this initializer if you're using a non-Azure OpenAI API Key

    // var chatCompletionsOptions = new ChatCompletionsOptions()
    // {
    //     Messages = {
    //         new ChatMessage(ChatRole.System, "あなたは顧客の困りごとを解決する AI アシスタントです。ユーザーの質問に対して、100文字以内で答えてください。また、リンクの情報を回答する際は、URLは回答から削除してください。"),
    //         new ChatMessage(ChatRole.User, $"次の質問に対して、100文字以内で答えて: '{speech_input}?'。回答にURLを含む場合は、URLを削除してタイトルのみ回答に含むようにして。"),
    //                 },
    //     MaxTokens = 50 // 100文字を目安にトークン数を設定。実際のトークン数は要調整。
    // };

    var chatCompletionsOptions = new ChatCompletionsOptions()
    {
        Messages = {
            new ChatMessage(ChatRole.System, "あなたは顧客の困りごとを解決する AI アシスタントです。回答には、具体的なドキュメント名を提供してください。回答は100文字以内で、URLや余分な情報は削除してドキュメント名のみを含めてください。"),
            new ChatMessage(ChatRole.User, $"次の質問に対して、100文字以内で答えて: '{speech_input}?'。"),
                    },
        MaxTokens = 100
    };

    ChatCompletions response;
    ChatChoice responseChoice;


    // 使用する Function を定義する
    FunctionDefinition getMicrosoftSearchApi = GetFunctionDefinition();
    chatCompletionsOptions.Functions.Add(getMicrosoftSearchApi);

    response = await ai_client.GetChatCompletionsAsync(builder.Configuration.GetValue<string>("AzureOpenAIDeploymentModelName"), chatCompletionsOptions);

    // function_call を行うか判別する
    responseChoice = response.Choices[0];

    // function_call のうちはループを回す
    while (responseChoice.FinishReason == CompletionsFinishReason.FunctionCall)
    {
        // Add message as a history.
        chatCompletionsOptions.Messages.Add(responseChoice.Message);

        if (responseChoice.Message.FunctionCall.Name == MicrosoftSearchApiFunction.Name)
        {
            Console.WriteLine($"呼び出す関数: {MicrosoftSearchApiFunction.Name}");
            string unvalidatedArguments = responseChoice.Message.FunctionCall.Arguments;
            Console.WriteLine($"引数: {unvalidatedArguments}");
            SearchInput input = JsonSerializer.Deserialize<SearchInput>(unvalidatedArguments,
                    new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
            Console.WriteLine($"インプット: {input}");
            var functionResultData = CallMicrosoftSearchAsync(input);
            var functionResponseMessage = new ChatMessage(
                ChatRole.Function,
                JsonSerializer.Serialize(
                    functionResultData,
                    new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            functionResponseMessage.Name = MicrosoftSearchApiFunction.Name;
            chatCompletionsOptions.Messages.Add(functionResponseMessage);
        }

        Console.WriteLine($"Function call: {chatCompletionsOptions.Messages}");

        response = await ai_client.GetChatCompletionsAsync(
            deploymentOrModelName: builder.Configuration.GetValue<string>("AzureOpenAIDeploymentModelName"),
            chatCompletionsOptions
        );

        responseChoice = response.Choices[0];
    }

    // 最終的な出力
    var response_content = responseChoice.Message.Content;

    Console.WriteLine($"最終的な出力: {response_content}");

    return response_content;
}

async Task HandleRecognizeAsync(CallMedia callConnectionMedia, string callerId, string message)
{
    // Play greeting message
    var greetingPlaySource = new TextSource(message)
    {
        VoiceName = "ja-JP-DaichiNeural"
    };

    var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(callerId))
        {
            InterruptPrompt = false,
            InitialSilenceTimeout = TimeSpan.FromSeconds(15),
            Prompt = greetingPlaySource,
            OperationContext = "GetFreeFormText",
            EndSilenceTimeout = TimeSpan.FromMilliseconds(500),
            SpeechLanguage = "ja-JP"
        };

    var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
}

async Task HandlePlayAsync(CallMedia callConnectionMedia)
{
    // Play goodbye message
    var GoodbyePlaySource = new TextSource("お電話ありがとうございました。失礼致します。")
    {
        VoiceName = "ja-JP-DaichiNeural"
    };

    await callConnectionMedia.PlayToAllAsync(GoodbyePlaySource);
}

async Task HandleHangupAsync(CallConnection callConnection)
{
    var GoodbyePlaySource = new TextSource("お電話ありがとうございました。失礼致します。")
    {
        VoiceName = "ja-JP-DaichiNeural"
    };

    await callConnection.HangUpAsync(true);
}

app.Run();
using Microsoft.Extensions.Hosting;
using System.IO.Ports;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Text;
using static System.Net.Mime.MediaTypeNames;

namespace DucoboxSilentSerial
{
    public class Worker : BackgroundService
    {
        private static readonly TimeSpan _scanInterval = TimeSpan.FromSeconds(10); 

        private readonly ILogger<Worker> _logger;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var httpClient = new HttpClient();

            var stopwatch = new Stopwatch();

            while (!stoppingToken.IsCancellationRequested)
            {
                var portNames = Directory.EnumerateFiles("/dev", "ttyUSB*");

                stopwatch.Restart();

                var readFromPortTasks = CreateReadFromPortTasks(portNames);
                await Task.WhenAll(readFromPortTasks);

                var things = readFromPortTasks.SelectMany(r => r.Result);
                _logger.LogInformation("Found {NumThings} things", things.Count());

                stopwatch.Stop();

                _logger.LogInformation("Scanning took {ScanTimeSecs} seconds", stopwatch.Elapsed.TotalSeconds);

                var waitTime = _scanInterval - stopwatch.Elapsed;
                if (waitTime.Ticks < 0)
                {
                    waitTime = _scanInterval;
                }
                _logger.LogInformation("Sending things to service and waiting for {WaitTimeSecs} seconds", waitTime.TotalSeconds);

                _ = PostThings(things, httpClient);

                await Task.Delay(waitTime, stoppingToken);
            }
        }

        private async Task PostThings(IEnumerable<Thing> things, HttpClient httpClient)
        {
            var thingsJson = new StringContent(
                JsonSerializer.Serialize(things),
                Encoding.UTF8,
                Application.Json);

            using (_logger.BeginScope("Api call"))
            {
                _logger.LogInformation("API call started");
                try
                {
                    var result = await httpClient.PatchAsync("http://thingify-core:80/api-rest/things", thingsJson);

                    result.EnsureSuccessStatusCode();
                    _logger.LogInformation("API call completed succesfully");
                }
                catch(HttpRequestException apiCallError)
                {
                    _logger.LogError(apiCallError, "API call failed");
                }
            }
        }

        private IEnumerable<Task<List<Thing>>> CreateReadFromPortTasks(IEnumerable<string> portNames)
        {
            foreach(var portName in portNames)
            {
                var readFromPortTask = ReadFromPort(portName);
                yield return readFromPortTask;
            }
        }

        private async Task<List<Thing>> ReadFromPort(string portName)
        {
            using (_logger.BeginScope("{PortName}", portName))
            {
                List<Thing> things;

                using(var serialPort = new SerialPort(portName, 115200))
                {
                    serialPort.ReadTimeout = 500;
                    serialPort.WriteTimeout = 3000;

                    _logger.LogInformation("Opening port");

                    try
                    {
                        serialPort.Open();
                    }
                    catch(UnauthorizedAccessException)
                    {
                        _logger.LogInformation("Unauthorized access");
                        return new List<Thing>();
                    }

                    things = await GetThingsFromSerialPort(serialPort);

                    serialPort.Close();
                }

                if (things.Any())
                {
                    _logger.LogInformation("Data succesfully read");
                }

                return things;
            }
        }

        private async Task<List<Thing>> GetThingsFromSerialPort(SerialPort serialPort)
        {
            var things = new List<Thing>();
            
            // fetch serial
            var boardInfoLines = await WriteCommandAndReadResultLines(serialPort, "boardinfo");
            if (boardInfoLines.Any() == false)
            {
                _logger.LogInformation("Got no response from port, most probably no DucoBox is connected");
                return things;
            }

            var boardInfoSerialLinePrefix = "Serial";
            var boardInfoLinesSerial = boardInfoLines.Where(l => l.TrimStart().StartsWith(boardInfoSerialLinePrefix)).ToList();
            if (boardInfoLinesSerial.Count != 1)
            {
                _logger.LogInformation("Got none or multiple serials from boardinfo command, most probably no supported DucoBox is connected");
                return things;
            }

            var boardInfoLineSerial = boardInfoLinesSerial.First();
            var boardInfoLineSerialSplit = boardInfoLineSerial.Split(':', StringSplitOptions.TrimEntries);
            if (boardInfoLineSerialSplit.Length < 2)
            {
                _logger.LogInformation("Could not parse serial from boardinfo command result, most probably no supported DucoBox is connected");
                return things;
            }
            var boardSerial = boardInfoLineSerialSplit[1];

            // fetch network
            var networkTable = await WriteCommandAndParseResultAsTable(serialPort, "network");

            // fetch signal strengths
            var commInfoLines = await WriteCommandAndReadResultLines(serialPort, "commnlinfo");
            var signalStrengthPerNode = ParseSignalStrengths(commInfoLines);

            // read through network
            foreach(var networkLine in networkTable)
            {
                var nodeNumber = networkLine["node"];
                var nodeType = networkLine["type"];

                using(_logger.BeginScope("{NodeNumber}", nodeNumber))
                {
                    var baseId = $"{boardSerial}-{nodeNumber}";
                    var nodeThing = new Thing(baseId, nodeType);

                    if (nodeType == "BOX")
                    {
                        var boxModeThing = new Thing(baseId + "-mode", "Mode");

                        var mode = networkLine["stat"];
                        boxModeThing.MeasurementPossibleValues.AddRange(new [] { "AUTO", "MAN1", "MAN2", "MAN3" });
                        boxModeThing.Measurements.Add(new Measurement(DateTime.Now, mode));

                        nodeThing.Things.Add(boxModeThing);
                        
                        // fetch fanspeed
                        var fanspeedThing = new Thing(baseId + "-fanspeed", "Fanspeed");
                        fanspeedThing.MeasurementUnit = "rpm";
                        nodeThing.Things.Add(fanspeedThing);

                        var fanspeedResult = await WriteCommandAndReadResultLines(serialPort, "fanspeed");
                        foreach(var fanspeedResultLine in fanspeedResult)
                        {
                            if (fanspeedResultLine.TrimStart().StartsWith("FanSpeed:"))
                            {
                                var index = fanspeedResultLine.IndexOf("Filtered");
                                if (index > 0)
                                {
                                    var speedParseStart = index + 9;
                                    var indexEnd = fanspeedResultLine.IndexOf("[", speedParseStart) - 1;
                                    if (indexEnd > speedParseStart)
                                    {
                                        var speedRaw = fanspeedResultLine.Substring(speedParseStart, indexEnd - speedParseStart);
                                        int speed;
                                        if (int.TryParse(speedRaw, out speed) == false)
                                        {
                                            _logger.LogInformation("Could not parse fanspeed as integral number, most probably no supported DucoBox is connected");
                                        }
                                        else
                                        {
                                            fanspeedThing.Measurements.Add(new Measurement(DateTime.Now, speed));
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (nodeType.StartsWith("UC"))
                    {
                        // fetch temp
                        var tempThing = new Thing(baseId + "-temp", "Temp");
                        tempThing.MeasurementUnit = "DegC";
                        nodeThing.Things.Add(tempThing);

                        var tempRaw = networkLine["temp"];
                        int temp;
                        if (int.TryParse(tempRaw, out temp) == false)
                        {
                            _logger.LogInformation("Could not parse temperature as integral number, most probably no supported DucoBox is connected");
                        }
                        else
                        {
                            var tempScaled = (decimal)temp / 10;
                            tempThing.Measurements.Add(new Measurement(DateTime.Now, tempScaled));
                        }

                        // fetch sensor request
                        var sensorRequestThing = new Thing(baseId + "-snsr", "Sensor request");
                        sensorRequestThing.MeasurementUnit = "%";
                        nodeThing.Things.Add(sensorRequestThing);

                        var sensorRequestRaw = networkLine["snsr"];
                        int sensorRequest;
                        if (int.TryParse(sensorRequestRaw, out sensorRequest) == false)
                        {
                            _logger.LogInformation("Could not parse sensor request as integral number, most probably no supported DucoBox is connected");
                        }
                        else
                        {
                            sensorRequestThing.Measurements.Add(new Measurement(DateTime.Now, sensorRequest));                        
                        }

                        // signal strength
                        var signalStrenghtThing = new Thing(baseId + "-signl", "Signal strength");
                        signalStrenghtThing.MeasurementUnit = "dBm";
                        nodeThing.Things.Add(signalStrenghtThing);

                        if (int.TryParse(nodeNumber, out int nodeNumberInt))
                        {
                            if (signalStrengthPerNode.ContainsKey(nodeNumberInt))
                            {
                                var signalStrength = signalStrengthPerNode[nodeNumberInt];
                                signalStrenghtThing.Measurements.Add(new Measurement(DateTime.Now, signalStrength));
                            }
                        }

                        // fetch co2
                        if (nodeType == "UCCO2")
                        {
                            var co2Thing = new Thing(baseId + "-co2", "CO2");
                            co2Thing.MeasurementUnit = "ppm";
                            nodeThing.Things.Add(co2Thing);

                            // fetch co2
                            var co2Result = await WriteCommandAndReadResultLines(serialPort, $"nodeparaget {nodeNumber} 74");
                            foreach(var co2ResultLine in co2Result)
                            {
                                var index = co2ResultLine.IndexOf("-->");
                                if (index > 0)
                                {
                                    var co2Raw = co2ResultLine.Substring(index + 4);
                                    int co2;
                                    if (int.TryParse(co2Raw, out co2) == false)
                                    {
                                        _logger.LogInformation("Could not parse co2 as integral number, most probably no supported DucoBox is connected");
                                    }
                                    else
                                    {
                                        co2Thing.Measurements.Add(new Measurement(DateTime.Now, co2));
                                    }                                
                                }
                            }
                        }
                    }

                    things.Add(nodeThing);
                }
            }

            return things;
        }

        private Dictionary<int, int> ParseSignalStrengths(List<string> commNlInfo)
        {
            var result = new Dictionary<int, int>();

            var started = false;

            foreach(var line in commNlInfo)
            {
                if (line.TrimStart().StartsWith("NL Network"))
                {
                    started = true;
                    continue;
                }

                if (started == false)
                {
                    continue;
                }

                var lineParts = line.Split('-', 2, StringSplitOptions.TrimEntries);
                if (lineParts.Length < 2)
                {
                    continue;
                }

                int nodeNumber;
                if (int.TryParse(lineParts[0], out nodeNumber) == false)
                {
                    continue;
                }

                var signalStrengthHex = lineParts[1].Split(' ', 2)[0];

                var signalStrength = (Convert.ToInt32(signalStrengthHex, 16) - 128) / 2 - 74;
                result.Add(nodeNumber, signalStrength);
            }

            return result;
        }

        private async Task<List<Dictionary<string, string>>> WriteCommandAndParseResultAsTable(SerialPort serialPort, string command)
        {
            var resultLines = await WriteCommandAndReadResultLines(serialPort, command);

            var headers = new List<string>();

            var result = new List<Dictionary<string, string>>();

            bool started = false;
            foreach(var resultLine in resultLines)
            {
                if (resultLine.TrimStart().StartsWith("--- start"))
                {
                    started = true;
                    continue;
                }

                if (started == false)
                {
                    continue;
                }

                if (resultLine.TrimStart().StartsWith("--- end"))
                {
                    break;
                }

                var resultLineSplit = resultLine.Split('|', StringSplitOptions.TrimEntries);
                if (headers.Count == 0)
                {
                    // no headers parsed yet, parse this first row as headers
                    foreach(var resultLineSplitValue in resultLineSplit)
                    {
                        headers.Add(resultLineSplitValue);
                    }
                }
                else
                {
                    var index = 0;
                    var resultRow = new Dictionary<string, string>();
                    foreach(var header in headers)
                    {
                        if (resultLineSplit.Length <= index)
                        {
                            break;
                        }

                        var value = resultLineSplit[index];
                        resultRow.Add(header, value);
                        index += 1;
                    }
                    result.Add(resultRow);
                }                
            }

            return result;
        }

        private async Task<List<string>> WriteCommandAndReadResultLines(SerialPort serialPort, string command)
        {
            var resultLines = new List<string>();

            try
            {                
                serialPort.Write("\r");
                await Task.Delay(TimeSpan.FromMilliseconds(10));
                foreach(var commandChar in command)
                {
                    serialPort.Write(commandChar.ToString());
                    await Task.Delay(TimeSpan.FromMilliseconds(10));
                }
                serialPort.Write("\r");
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }
            catch(TimeoutException)
            {
                _logger.LogError("Timeout sending command");
                return resultLines;
            }

            var continueReading = true;
            while(continueReading)
            {
                try
                {
                    var resultLine = serialPort.ReadTo("\r");
                    resultLines.Add(resultLine);
                }
                catch(TimeoutException)
                {
                    // no more lines to read
                    continueReading = false;
                }
            }

            return resultLines;
        }
    }
}